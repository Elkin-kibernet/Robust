using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

public sealed class WorkerWsClient : IDisposable
{
    private readonly RemoteConfigService _configService;
    private readonly UpdateManagerService _updateManager;
    private readonly ProcessLauncher _processLauncher;

    private CancellationTokenSource? _connectionCts;
    private ClientWebSocket? _webSocket;
    private Func<int>? _totalAccountsCountProvider;
    private bool _isDisposed;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public event Action<bool, string>? ConnectionStatusChanged;
    public event Func<ServerCommandMessage, Task>? ServerCommandReceived;
    public event Action<AccountStateMessage>? AccountStateReceived;

    public WorkerWsClient(
        RemoteConfigService configService,
        UpdateManagerService updateManager,
        ProcessLauncher processLauncher)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));

        _processLauncher.ContractViolated += OnContractViolated;
    }

    public void SetTotalAccountsProvider(Func<int> provider)
    {
        _totalAccountsCountProvider = provider;
    }

    public void Connect(string wsEndpointUrl, string workerApiToken)
    {
        Disconnect();

        _connectionCts = new CancellationTokenSource();
        var ct = _connectionCts.Token;

        _ = Task.Run(() => MaintainConnectionLoopAsync(wsEndpointUrl, workerApiToken, ct), ct);
    }

    public void Disconnect()
    {
        try
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;

            _webSocket?.Dispose();
            _webSocket = null;
        }
        catch
        {
            // Игнорируем
        }

        ConnectionStatusChanged?.Invoke(false, "Отключено");
    }

    private void OnContractViolated(string reason, ActiveGameSession? violatedSession)
    {
        _ = SendContractViolationAsync(reason, violatedSession);
    }

    public async Task SendContractViolationAsync(string reason, ActiveGameSession? violatedSession = null)
    {
        var ws = _webSocket;
        if (ws is null || ws.State != WebSocketState.Open)
            return;

        try
        {
            var envelope = new WsEnvelope
            {
                EventType = "contract_violated",
                TimestampUtc = DateTime.UtcNow,
                Payload = new ContractViolationPayload
                {
                    OrderId = _processLauncher.ActiveOrderId,
                    OrderMode = _processLauncher.ActiveOrderMode,
                    ViolatedUserId = violatedSession?.Account.UserId,
                    ViolatedProcessId = violatedSession?.ProcessId,
                    TimeoutSeconds = _processLauncher.LastAssignedTimeoutSeconds,
                    DailyEarlyExits = _processLauncher.CurrentDailyEarlyExits,
                    Reason = reason
                }
            };

            var json = JsonSerializer.Serialize(envelope);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkerWsClient] Ошибка отправки уведомления о досрочном выходе: {ex.Message}");
        }
    }

    private async Task MaintainConnectionLoopAsync(string wsEndpointUrl, string workerApiToken, CancellationToken ct)
    {
        int retryAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            _webSocket = ws;

            ws.Options.SetRequestHeader("X-Client-Version", _updateManager.CurrentVersion);
            if (!string.IsNullOrWhiteSpace(workerApiToken))
            {
                ws.Options.SetRequestHeader("Authorization", $"Bearer {workerApiToken.Trim()}");
            }
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                ConnectionStatusChanged?.Invoke(false, "Подключение к платформе...");
                await ws.ConnectAsync(new Uri(wsEndpointUrl), ct).ConfigureAwait(false);

                retryAttempt = 0;
                ConnectionStatusChanged?.Invoke(true, "онлайн");

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                var receiveTask = ReceiveLoopAsync(ws, linkedCts.Token);
                var heartbeatTask = HeartbeatLoopAsync(ws, linkedCts.Token);

                await Task.WhenAny(receiveTask, heartbeatTask).ConfigureAwait(false);
                linkedCts.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                retryAttempt++;
                var backoffSeconds = Math.Min(30, Math.Pow(2, Math.Min(retryAttempt, 5)));
                ConnectionStatusChanged?.Invoke(false, $"Обрыв связи ({ex.Message}). Реконнект через {backoffSeconds:0} сек...");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            await SendHeartbeatOnceAsync(ws, ct).ConfigureAwait(false);

            var intervalSeconds = Math.Max(2, _configService.CurrentConfig.HeartbeatIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task SendHeartbeatOnceAsync(ClientWebSocket ws, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            return;

        var now = DateTime.UtcNow;
        var activeSessions = _processLauncher.GetActiveSessions()
            .Select(s => new HeartbeatSessionItem
            {
                UserId = s.Account.UserId,
                PlaceId = s.PlaceId,
                JobId = s.CurrentJobId,
                ServerIp = s.ConnectedServerIp,
                TicketHash = s.AuthTicketSha256,
                IsVerified = s.IsFullyVerified,
                PresenceVerified = s.IsPresenceApiVerified,
                ProcessId = s.ProcessId,
                UptimeSeconds = (int)Math.Max(0, (now - s.StartedAtUtc).TotalSeconds)
            })
            .ToList();

        var envelope = new WsEnvelope
        {
            EventType = "worker_heartbeat",
            TimestampUtc = now,
            Payload = new HeartbeatPayload
            {
                ClientVersion = _updateManager.CurrentVersion,
                TotalAccountsLoaded = _totalAccountsCountProvider?.Invoke() ?? activeSessions.Count,
                IsContractActive = _processLauncher.IsContractActive,
                IsInTimeout = _processLauncher.IsInTimeout,
                RemainingTimeoutSeconds = _processLauncher.RemainingTimeoutSeconds,
                DailyEarlyExits = _processLauncher.CurrentDailyEarlyExits,
                ActiveOrderId = _processLauncher.ActiveOrderId,
                ActiveOrderMode = _processLauncher.ActiveOrderMode,
                LockedSlotsCount = _processLauncher.LockedSlotsCount,
                ActiveSessions = activeSessions
            }
        };

        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var messageJson = Encoding.UTF8.GetString(ms.ToArray());
                    await HandleIncomingMessageAsync(messageJson).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleIncomingMessageAsync(string json)
    {
        try
        {
            // Профиль и баланс приходят отдельным ответом на heartbeat.
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty("command", out var commandElement) &&
                    string.Equals(commandElement.GetString(), "account_state", StringComparison.OrdinalIgnoreCase))
                {
                    var accountState = JsonSerializer.Deserialize<AccountStateMessage>(json);
                    if (accountState is not null)
                        AccountStateReceived?.Invoke(accountState);
                    return;
                }
            }

            var command = JsonSerializer.Deserialize<ServerCommandMessage>(json);
            if (command is null || string.IsNullOrWhiteSpace(command.Command))
                return;

            if (string.Equals(command.Command, "force_update", StringComparison.OrdinalIgnoreCase))
            {
                _processLauncher.CompleteContractNormally();
                var feedUrl = !string.IsNullOrWhiteSpace(command.UpdateUrl)
                    ? command.UpdateUrl
                    : _configService.CurrentConfig.UpdateFeedUrl;

                await _updateManager.CheckAndApplyUpdatesAsync(feedUrl).ConfigureAwait(false);
                return;
            }

            if (ServerCommandReceived is not null)
            {
                await ServerCommandReceived.Invoke(command).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkerWsClient] Ошибка обработки сообщения сервера: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _processLauncher.ContractViolated -= OnContractViolated;
        Disconnect();
    }
}
