using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

public sealed class LogWatcherService : IDisposable
{
    private readonly ProcessLauncher _processLauncher;
    private readonly RemoteConfigService _configService;
    private readonly RobloxAuthService _authService;
    private readonly string _robloxLogsDirectory;
    private readonly PeriodicTimer _scanTimer;
    private readonly SemaphoreSlim _logScanLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, long> _fileReadOffsets = new(StringComparer.OrdinalIgnoreCase);

    // Пока RobloxPlayerBeta.exe стартует, он может перезапускать сам себя
    // (обновление/хендофф процесса). Такой выход не считается ручным закрытием.
    private const int StartupGraceSeconds = 45;
    private const int MaxStartupAutoReconnects = 3;
    private readonly ConcurrentDictionary<long, int> _startupReconnectAttempts = new();

    private static readonly HashSet<int> NonRetryableErrorCodes = new()
    {
        267,
        273
    };

    private Regex? _serverJoinedRegex;
    private Regex? _connectionAcceptedRegex;
    private Regex? _disconnectRegex;
    private Task? _watchLoopTask;
    private bool _isDisposed;

    public event Action<ActiveGameSession, string>? ServerJobIdDetected;
    public event Action<ActiveGameSession, string>? AntiFraudVerified;
    public event Action<ActiveGameSession, int>? SessionDisconnected;

    /// <summary>
    /// Срабатывает, когда пользователь вручную закрыл окно Roblox (без сетевой ошибки в логах).
    /// </summary>
    public event Action<ActiveGameSession>? ManualWindowCloseDetected;

    public LogWatcherService(
        ProcessLauncher processLauncher,
        RemoteConfigService configService,
        RobloxAuthService authService)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        _robloxLogsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "logs");

        CompileRegexPatterns(_configService.CurrentConfig);
        _configService.ConfigUpdated += CompileRegexPatterns;
        _processLauncher.ProcessCrashedOrExited += OnProcessCrashedOrExited;

        _scanTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
    }

    public void Start()
    {
        if (_watchLoopTask is not null)
            return;

        _watchLoopTask = Task.Run(() => RunLogWatchLoopAsync(_cts.Token));
    }

    private void CompileRegexPatterns(RemoteConfig config)
    {
        var timeout = TimeSpan.FromMilliseconds(200);
        try
        {
            if (!string.IsNullOrWhiteSpace(config.LogPatterns.ServerJoinedPattern))
            {
                _serverJoinedRegex = new Regex(
                    config.LogPatterns.ServerJoinedPattern,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    timeout);
            }

            if (!string.IsNullOrWhiteSpace(config.LogPatterns.ConnectionAcceptedPattern))
            {
                _connectionAcceptedRegex = new Regex(
                    config.LogPatterns.ConnectionAcceptedPattern,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    timeout);
            }

            if (!string.IsNullOrWhiteSpace(config.LogPatterns.DisconnectPattern))
            {
                _disconnectRegex = new Regex(
                    config.LogPatterns.DisconnectPattern,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    timeout);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogWatcherService] Ошибка компиляции регулярок: {ex.Message}");
        }
    }

    private async Task RunLogWatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _scanTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var activeSessions = _processLauncher.GetActiveSessions();
                if (activeSessions.Count == 0)
                    continue;

                await ScanRecentLogFilesAsync(activeSessions, ct).ConfigureAwait(false);
                await VerifySessionsViaRobloxPresenceApiAsync(activeSessions, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogWatcherService] Ошибка в цикле мониторинга: {ex.Message}");
        }
    }

    private async Task ScanRecentLogFilesAsync(
        IReadOnlyCollection<ActiveGameSession> sessionsToInspect,
        CancellationToken ct)
    {
        if (sessionsToInspect.Count == 0 || !Directory.Exists(_robloxLogsDirectory))
            return;

        await _logScanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var earliestSessionStart = sessionsToInspect.Min(s => s.StartedAtUtc).AddMinutes(-1);
            var recentLogFiles = new DirectoryInfo(_robloxLogsDirectory)
                .EnumerateFiles("*Player*.log", SearchOption.TopDirectoryOnly)
                .Where(f => f.LastWriteTimeUtc >= earliestSessionStart)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(4, sessionsToInspect.Count * 2))
                .ToList();

            foreach (var logFile in recentLogFiles)
            {
                if (ct.IsCancellationRequested)
                    break;

                await ReadNewLinesFromLogFileAsync(logFile.FullName, sessionsToInspect, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _logScanLock.Release();
        }
    }

    private async Task VerifySessionsViaRobloxPresenceApiAsync(
        IReadOnlyCollection<ActiveGameSession> activeSessions,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var session in activeSessions)
        {
            if (ct.IsCancellationRequested)
                break;

            var uptimeSec = (now - session.StartedAtUtc).TotalSeconds;
            if (uptimeSec < 10)
                continue;

            if ((now - session.LastPresenceCheckUtc).TotalSeconds < 15)
                continue;

            session.LastPresenceCheckUtc = now;

            var (isInGame, officialJobId, detail) = await _authService.VerifyUserInGamePresenceAsync(
                session.Account,
                session.PlaceId,
                ct).ConfigureAwait(false);

            if (isInGame)
            {
                bool wasVerifiedBefore = session.IsPresenceApiVerified;
                session.IsPresenceApiVerified = true;

                // Окно успешно догрузилось до игры — сбрасываем счётчик стартовых реконнектов.
                _startupReconnectAttempts.TryRemove(session.Account.UserId, out _);

                if (!string.IsNullOrWhiteSpace(officialJobId) && string.IsNullOrWhiteSpace(session.CurrentJobId))
                {
                    session.CurrentJobId = officialJobId;
                    ServerJobIdDetected?.Invoke(session, officialJobId);
                }

                if (!wasVerifiedBefore)
                {
                    AntiFraudVerified?.Invoke(session, $"Presence API OK ({detail})");
                }
            }
            else
            {
                session.IsPresenceApiVerified = false;

                if (uptimeSec > 180 && !session.IsFullyVerified)
                {
                    _processLauncher.StopSession(session.Account.UserId);
                    _ = ScheduleAutoReconnectAsync(session.Account, session.PlaceId);
                }
            }
        }
    }

    private async Task ReadNewLinesFromLogFileAsync(
        string filePath,
        IReadOnlyCollection<ActiveGameSession> activeSessions,
        CancellationToken ct)
    {
        try
        {
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var previousOffset = _fileReadOffsets.GetOrAdd(filePath, 0L);

            if (fileStream.Length <= previousOffset)
                return;

            fileStream.Seek(previousOffset, SeekOrigin.Begin);

            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    InspectLogLine(line, activeSessions);
                }
            }

            _fileReadOffsets[filePath] = fileStream.Position;
        }
        catch (IOException)
        {
            // Игнорируем
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogWatcherService] Ошибка чтения файла '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    private void InspectLogLine(string line, IReadOnlyCollection<ActiveGameSession> activeSessions)
    {
        try
        {
            var joinRegex = _serverJoinedRegex;
            if (joinRegex is not null)
            {
                var match = joinRegex.Match(line);
                if (match.Success)
                {
                    var jobId = match.Groups["JobId"].Value;
                    var placeIdStr = match.Groups["PlaceId"].Value;

                    if (long.TryParse(placeIdStr, out var placeId) && !string.IsNullOrWhiteSpace(jobId))
                    {
                        var targetSession = activeSessions
                            .FirstOrDefault(s => s.PlaceId == placeId && string.IsNullOrEmpty(s.CurrentJobId))
                            ?? activeSessions.FirstOrDefault(s => s.PlaceId == placeId);

                        if (targetSession is not null)
                        {
                            targetSession.CurrentJobId = jobId;
                            ServerJobIdDetected?.Invoke(targetSession, jobId);
                        }
                    }
                    return;
                }
            }

            var connRegex = _connectionAcceptedRegex;
            if (connRegex is not null)
            {
                var match = connRegex.Match(line);
                if (match.Success)
                {
                    var serverIp = match.Groups["ServerIp"].Value;
                    if (!string.IsNullOrWhiteSpace(serverIp))
                    {
                        var targetSession = activeSessions
                            .FirstOrDefault(s => !string.IsNullOrEmpty(s.CurrentJobId) && string.IsNullOrEmpty(s.ConnectedServerIp))
                            ?? activeSessions.OrderByDescending(s => s.StartedAtUtc).FirstOrDefault();

                        if (targetSession is not null && string.IsNullOrEmpty(targetSession.ConnectedServerIp))
                        {
                            targetSession.ConnectedServerIp = serverIp;
                            AntiFraudVerified?.Invoke(targetSession, $"UDP-сервер подтверждён ({serverIp})");
                        }
                    }
                    return;
                }
            }

            var discRegex = _disconnectRegex;
            if (discRegex is not null)
            {
                var match = discRegex.Match(line);
                if (match.Success && int.TryParse(match.Groups["ErrorCode"].Value, out var errorCode))
                {
                    var targetSession = activeSessions.OrderByDescending(s => s.StartedAtUtc).FirstOrDefault();
                    if (targetSession is not null)
                    {
                        targetSession.HasNetworkDisconnectInLogs = true;
                        targetSession.LastDisconnectErrorCode = errorCode;

                        SessionDisconnected?.Invoke(targetSession, errorCode);

                        if (NonRetryableErrorCodes.Contains(errorCode))
                        {
                            _processLauncher.StopSession(targetSession.Account.UserId);
                        }
                        else
                        {
                            _processLauncher.StopSession(targetSession.Account.UserId);
                            _ = ScheduleAutoReconnectAsync(targetSession.Account, targetSession.PlaceId);
                        }
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            Debug.WriteLine("[LogWatcherService] Превышен лимит времени Regex на строке лога.");
        }
    }

    private void OnProcessCrashedOrExited(ActiveGameSession exitedSession, int exitCode)
    {
        if (_isDisposed || exitedSession.IsIntentionalStop)
            return;

        _ = HandleUnexpectedProcessExitAsync(exitedSession);
    }

    /// <summary>
    /// Если окно закрылось и в логах НЕТ сетевой ошибки (Lost connection / Disconnection Notification),
    /// значит окно закрыл вручную сам пользователь (на крестик [X], Alt+F4 или через панель задач, код 0 или 1).
    /// </summary>
    private async Task HandleUnexpectedProcessExitAsync(ActiveGameSession exitedSession)
    {
        try
        {
            await Task.Delay(200, _cts.Token).ConfigureAwait(false);

            var inspectList = new List<ActiveGameSession>(capacity: 1) { exitedSession };
            await ScanRecentLogFilesAsync(inspectList, _cts.Token).ConfigureAwait(false);

            if (exitedSession.IsIntentionalStop || !_processLauncher.IsContractActive)
                return;

            // Стартовый grace-период: Roblox при запуске может перезапустить процесс.
            // Не считаем это ручным закрытием и НЕ убиваем остальные окна.
            var sessionUptime = DateTime.UtcNow - exitedSession.StartedAtUtc;
            if (sessionUptime.TotalSeconds < StartupGraceSeconds)
            {
                if (exitedSession.IsFullyVerified)
                {
                    _startupReconnectAttempts.TryRemove(exitedSession.Account.UserId, out _);
                }
                else
                {
                    var attempts = _startupReconnectAttempts.AddOrUpdate(
                        exitedSession.Account.UserId,
                        1,
                        (_, current) => current + 1);

                    if (attempts <= MaxStartupAutoReconnects)
                    {
                        Debug.WriteLine(
                            $"[LogWatcherService] Клиент Roblox завершился во время запуска ({sessionUptime.TotalSeconds:F0} с). " +
                            $"Тихо перезапускаем окно (попытка {attempts}/{MaxStartupAutoReconnects}).");
                        await ScheduleAutoReconnectAsync(exitedSession.Account, exitedSession.PlaceId).ConfigureAwait(false);
                        return;
                    }
                }
            }

            // Если в логах НЕТ сетевого дисконнекта — окно было закрыто пользователем вручную!
            if (!exitedSession.HasNetworkDisconnectInLogs)
            {
                ManualWindowCloseDetected?.Invoke(exitedSession);
                return;
            }

            // Если в логах была сетевая ошибка — перезапускаем автоматически без вопросов
            await ScheduleAutoReconnectAsync(exitedSession.Account, exitedSession.PlaceId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogWatcherService] Ошибка анализа выхода процесса: {ex.Message}");
        }
    }

    public async Task ScheduleAutoReconnectAsync(WorkerAccount account, long placeId)
    {
        try
        {
            var delaySeconds = Math.Max(5, _configService.CurrentConfig.ReconnectDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _cts.Token).ConfigureAwait(false);

            if (_cts.IsCancellationRequested || !_processLauncher.IsContractActive)
                return;

            await _processLauncher.LaunchGameWindowAsync(account, placeId, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отменено
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LogWatcherService] Ошибка при авто-реконнекте {account.Username}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _configService.ConfigUpdated -= CompileRegexPatterns;
        _processLauncher.ProcessCrashedOrExited -= OnProcessCrashedOrExited;
        _cts.Cancel();
        _scanTimer.Dispose();
        _logScanLock.Dispose();
        _cts.Dispose();
    }
}
