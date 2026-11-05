using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

public sealed class ProcessLauncher : IDisposable
{
    private readonly RemoteConfigService _configService;
    private readonly MutexManager _mutexManager;
    private readonly ClientOptimizer _clientOptimizer;
    private readonly RobloxAuthService _authService;

    private readonly ConcurrentDictionary<long, ActiveGameSession> _activeSessions = new();
    private readonly object _ipLimitLock = new();
    private readonly object _contractLock = new();

    private nint _jobObjectHandle;
    private bool _isDisposed;

    /// <summary>
    /// Удержание от заработанного при досрочном выходе (15% за переброс слота).
    /// </summary>
    public const int EarlyExitFeePercent = 15;

    public bool IsContractActive { get; private set; }
    public string? ActiveOrderId { get; private set; }
    public string ActiveOrderMode { get; private set; } = "MANUAL";
    public int LockedSlotsCount { get; private set; }
    public DateTime? ContractStartedAtUtc { get; private set; }
    public DateTime? TimeoutUntilUtc { get; private set; }
    public int LastAssignedTimeoutSeconds { get; private set; }

    public bool IsInTimeout => TimeoutUntilUtc.HasValue && DateTime.UtcNow < TimeoutUntilUtc.Value;

    public int RemainingTimeoutSeconds =>
        TimeoutUntilUtc.HasValue
            ? Math.Max(0, (int)Math.Ceiling((TimeoutUntilUtc.Value - DateTime.UtcNow).TotalSeconds))
            : 0;

    /// <summary>
    /// Текущее число досрочных выходов за сегодня (сбрасывается в 00:00 каждого дня).
    /// </summary>
    public int CurrentDailyEarlyExits
    {
        get
        {
            var settings = _clientOptimizer.CurrentSettings;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return string.Equals(settings.LastEarlyExitDate, today, StringComparison.Ordinal)
                ? settings.DailyEarlyExitCount
                : 0;
        }
    }

    public event Action<ActiveGameSession, int>? ProcessCrashedOrExited;
    public event Action<ActiveGameSession>? SessionStarted;
    public event Action<string, ActiveGameSession?>? ContractViolated;
    public event Action<bool>? ContractStateChanged;

    public ProcessLauncher(
        RemoteConfigService configService,
        MutexManager mutexManager,
        ClientOptimizer clientOptimizer,
        RobloxAuthService authService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _mutexManager = mutexManager ?? throw new ArgumentNullException(nameof(mutexManager));
        _clientOptimizer = clientOptimizer ?? throw new ArgumentNullException(nameof(clientOptimizer));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));

        InitializeWindowsJobObject();
    }

    public IReadOnlyCollection<ActiveGameSession> GetActiveSessions()
    {
        return _activeSessions.Values.ToArray();
    }

    /// <summary>
    /// Принимает серверный штраф (или локально сохранённый) и продлевает тайм-аут.
    /// Перезапуск лаунчера не сбрасывает ограничение.
    /// </summary>
    public void ApplyServerPenalty(DateTime? untilUtc)
    {
        if (!untilUtc.HasValue || untilUtc.Value <= DateTime.UtcNow)
            return;

        lock (_contractLock)
        {
            if (TimeoutUntilUtc.HasValue && TimeoutUntilUtc.Value >= untilUtc.Value)
                return;

            TimeoutUntilUtc = untilUtc.Value;
            var remaining = Math.Max(1, (int)Math.Ceiling((untilUtc.Value - DateTime.UtcNow).TotalSeconds));
            LastAssignedTimeoutSeconds = Math.Max(LastAssignedTimeoutSeconds, remaining);
        }
    }

    public void StartContract(int lockedSlotsCount, string? orderId = null, string orderMode = "FLEX_POOL")
    {
        lock (_contractLock)
        {
            IsContractActive = true;
            LockedSlotsCount = Math.Max(1, lockedSlotsCount);
            ActiveOrderId = orderId;
            ActiveOrderMode = string.IsNullOrWhiteSpace(orderMode) ? "FLEX_POOL" : orderMode.ToUpperInvariant();
            ContractStartedAtUtc = DateTime.UtcNow;
            TimeoutUntilUtc = null;
        }

        ContractStateChanged?.Invoke(true);
    }

    public void CompleteContractNormally()
    {
        lock (_contractLock)
        {
            IsContractActive = false;
            LockedSlotsCount = 0;
            ActiveOrderId = null;
            ActiveOrderMode = "NONE";
            ContractStartedAtUtc = null;
            TimeoutUntilUtc = null;
        }

        StopAllSessions();
        ContractStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Вызывается при досрочном выходе воркера (отказ продолжать задание или нажатие Стоп).
    /// Увеличивает дневной счётчик выходов и назначает прогрессивный тайм-аут (сбрасывается только в конце дня).
    /// </summary>
    public void TriggerContractViolation(string reason, ActiveGameSession? violatedSession = null)
    {
        // Изолированный выход одного окна: закрываем только его, остальные
        // сессии продолжают работать без штрафа и тайм-аута всего контракта.
        if (violatedSession is not null &&
            _activeSessions.TryGetValue(violatedSession.Account.UserId, out var aliveSession) &&
            ReferenceEquals(aliveSession, violatedSession))
        {
            StopSession(violatedSession.Account.UserId);
            ContractViolated?.Invoke(reason, violatedSession);
            return;
        }

        int timeoutSec;

        lock (_contractLock)
        {
            if (!IsContractActive && _activeSessions.IsEmpty)
                return;

            IsContractActive = false;
            LockedSlotsCount = 0;

            var settings = _clientOptimizer.CurrentSettings;
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            if (!string.Equals(settings.LastEarlyExitDate, today, StringComparison.Ordinal))
            {
                settings.DailyEarlyExitCount = 0;
                settings.LastEarlyExitDate = today;
            }

            settings.DailyEarlyExitCount++;
            _ = _clientOptimizer.UpdateSettingsAsync(settings);

            // Прогрессивная шкала тайм-аута за день: 15с -> 30с -> 60с -> 120с -> 300с
            timeoutSec = settings.DailyEarlyExitCount switch
            {
                1 => 15,
                2 => 30,
                3 => 60,
                4 => 120,
                _ => 300
            };

            LastAssignedTimeoutSeconds = timeoutSec;
            TimeoutUntilUtc = DateTime.UtcNow.AddSeconds(timeoutSec);
        }

        StopAllSessions();

        ContractStateChanged?.Invoke(false);
        ContractViolated?.Invoke(reason, violatedSession);
    }

    public string GetOrderModeDisplayName() => ActiveOrderMode switch
    {
        "PEAK_CCU" => "🎯 Пиковый Онлайн",
        "FLEX_POOL" => "🌊 Накопительный пул",
        "MANUAL" => "Тестовый сеанс",
        _ => ActiveOrderMode
    };

    public async Task<(bool IsSuccess, string Message, ActiveGameSession? Session)> LaunchGameWindowAsync(
        WorkerAccount account,
        long placeId,
        CancellationToken ct = default)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ProcessLauncher));

        if (account is null)
            throw new ArgumentNullException(nameof(account));

        if (placeId <= 0)
            return (false, "Указан некорректный PlaceId.", null);

        if (_activeSessions.ContainsKey(account.UserId))
            return (false, $"Аккаунт {account.Username} ({account.UserId}) уже запущен.", null);

        var config = _configService.CurrentConfig;
        var proxyGroupKey = GetProxyGroupKey(account.ProxyAddress);

        lock (_ipLimitLock)
        {
            var currentCountOnIp = _activeSessions.Values.Count(s =>
                string.Equals(s.ProxyGroupKey, proxyGroupKey, StringComparison.OrdinalIgnoreCase));

            if (currentCountOnIp >= config.MaxWindowsPerIp)
            {
                return (false, $"Достигнут лимит окон ({config.MaxWindowsPerIp}) для IP/прокси '{proxyGroupKey}'.", null);
            }
        }

        if (!_mutexManager.IsMultiInstanceActive && !_mutexManager.AcquireSingletonMutex())
        {
            return (false, "Не удалось перехватить системный мьютекс мультиоконности. Закройте открытые окна Roblox и повторите попытку.", null);
        }

        _mutexManager.LockSharedLoginFiles();

        var exePath = ResolveRobloxPlayerPath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            // Если клиент уже обновляется официальным апдейтером — просто ждём
            // появления RobloxPlayerBeta.exe (до 45 секунд).
            if (IsRobloxUpdaterRunning())
            {
                try
                {
                    exePath = await WaitForRobloxPlayerExecutableAsync(
                        TimeSpan.FromSeconds(45),
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_activeSessions.IsEmpty)
                        _mutexManager.UnlockSharedLoginFiles();

                    return (false, "Запуск отменён, так как сеанс был остановлен.", null);
                }
            }

            // Метод 3: клиент не найден — запускаем официальный апдейтер и ждём
            // до 45 секунд, пока он восстановит RobloxPlayerBeta.exe.
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                var bootstrapperPath = RobloxPathResolver.FindBootstrapper();
                if (!string.IsNullOrWhiteSpace(bootstrapperPath) && File.Exists(bootstrapperPath))
                {
                    try
                    {
                        Debug.WriteLine($"[ProcessLauncher] RobloxPlayerBeta.exe не найден. Запускаем апдейтер: {bootstrapperPath}");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = bootstrapperPath,
                            UseShellExecute = true
                        });

                        exePath = await WaitForRobloxPlayerExecutableAsync(
                            TimeSpan.FromSeconds(45),
                            ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (_activeSessions.IsEmpty)
                            _mutexManager.UnlockSharedLoginFiles();

                        return (false, "Запуск отменён, так как сеанс был остановлен.", null);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ProcessLauncher] Не удалось запустить апдейтер Roblox: {ex.Message}");
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            if (_activeSessions.IsEmpty)
                _mutexManager.UnlockSharedLoginFiles();

            return (false, "Roblox не найден. Установите или запустите официальный лаунчер Roblox.", null);
        }

        await _clientOptimizer.ApplyOptimizationSettingsAsync().ConfigureAwait(false);

        var ticketResult = await _authService.GenerateAuthTicketAsync(account, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsContractActive)
        {
            if (_activeSessions.IsEmpty)
                _mutexManager.UnlockSharedLoginFiles();

            return (false, "Запуск отменён, так как сеанс был остановлен.", null);
        }

        if (!ticketResult.IsSuccess || string.IsNullOrWhiteSpace(ticketResult.AuthTicket))
        {
            if (_activeSessions.IsEmpty)
                _mutexManager.UnlockSharedLoginFiles();

            return (false, ticketResult.ErrorMessage ?? "Ошибка получения AuthTicket.", null);
        }

        var isolatedTempDir = PrepareIsolatedDirectory(account.UserId);
        var ticketHash = ComputeTicketSha256(ticketResult.AuthTicket, account.UserId, placeId);

        var launchArguments = BuildSafeLaunchArguments(
            config.LaunchArgsTemplate,
            ticketResult.AuthTicket,
            placeId);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = launchArguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            startInfo.Environment["TEMP"] = isolatedTempDir;
            startInfo.Environment["TMP"] = isolatedTempDir;

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            // FPS-лимит действует только на старт этого окна и затем откатывается.
            _clientOptimizer.BeginTemporaryFpsLimit(_clientOptimizer.CurrentSettings.TargetFpsCap);

            if (!process.Start())
            {
                _clientOptimizer.EndTemporaryFpsLimit();
                process.Dispose();
                if (_activeSessions.IsEmpty)
                    _mutexManager.UnlockSharedLoginFiles();

                return (false, "Не удалось запустить процесс RobloxPlayerBeta.exe.", null);
            }

            // Даём клиенту прочитать временный FPS-флаг и сразу возвращаем
            // глобальные настройки Roblox в исходное состояние.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                _clientOptimizer.EndTemporaryFpsLimit();
            });

            AssignProcessToJob(process.Handle);

            var session = new ActiveGameSession
            {
                Account = account,
                PlaceId = placeId,
                ProcessId = process.Id,
                ProxyGroupKey = proxyGroupKey,
                IsolatedTempDirectory = isolatedTempDir,
                AuthTicketSha256 = ticketHash
            };

            _activeSessions[account.UserId] = session;
            process.Exited += (_, _) => OnRobloxProcessExited(session, process);

            SessionStarted?.Invoke(session);
            return (true, "Окно успешно запущено.", session);
        }
        catch (Exception ex)
        {
            _clientOptimizer.EndTemporaryFpsLimit();
            if (_activeSessions.IsEmpty)
                _mutexManager.UnlockSharedLoginFiles();

            return (false, $"Ошибка при старте процесса: {ex.Message}", null);
        }
    }

    private static string? ResolveRobloxPlayerPath()
    {
        var resolution = RobloxPathResolver.Resolve();
        return resolution.HasPlayer ? resolution.PlayerExecutablePath : null;
    }

    /// <summary>
    /// Проверяет, идёт ли прямо сейчас официальное обновление Roblox.
    /// </summary>
    private static bool IsRobloxUpdaterRunning()
    {
        foreach (var processName in new[] { "RobloxPlayerLauncher", "RobloxPlayerInstaller" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                    return true;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// Ждёт, пока официальный апдейтер восстановит RobloxPlayerBeta.exe
    /// (обновление Roblox обычно занимает 5–45 секунд).
    /// </summary>
    private static async Task<string?> WaitForRobloxPlayerExecutableAsync(
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var path = ResolveRobloxPlayerPath();
            if (!string.IsNullOrWhiteSpace(path))
                return path;

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        return ResolveRobloxPlayerPath();
    }

    public bool StopSession(long userId)
    {
        if (!_activeSessions.TryRemove(userId, out var session))
            return false;

        session.IsIntentionalStop = true;

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Игнорируем
        }

        if (_activeSessions.IsEmpty)
        {
            _mutexManager.UnlockSharedLoginFiles();
        }

        return true;
    }

    public void StopAllSessions()
    {
        foreach (var session in _activeSessions.Values)
        {
            session.IsIntentionalStop = true;
        }

        foreach (var userId in _activeSessions.Keys.ToArray())
        {
            StopSession(userId);
        }

        try
        {
            foreach (var proc in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Игнорируем
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // Игнорируем
        }

        _mutexManager.UnlockSharedLoginFiles();
    }

    private void OnRobloxProcessExited(ActiveGameSession session, Process process)
    {
        int exitCode = 0;
        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            // Игнорируем
        }
        finally
        {
            process.Dispose();
        }

        _activeSessions.TryRemove(session.Account.UserId, out _);

        if (_activeSessions.IsEmpty)
        {
            _mutexManager.UnlockSharedLoginFiles();
        }

        if (!session.IsIntentionalStop && !_isDisposed)
        {
            ProcessCrashedOrExited?.Invoke(session, exitCode);
        }
    }

    private static string ComputeTicketSha256(string authTicket, long userId, long placeId)
    {
        var raw = $"{authTicket}:{userId}:{placeId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildSafeLaunchArguments(string template, string authTicket, long placeId)
    {
        var browserTrackerId = Random.Shared.NextInt64(100_000_000_000L, 999_999_999_999L).ToString();
        var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var safeTicket = authTicket.Replace("\"", string.Empty).Replace(" ", string.Empty);

        var rawPlaceLauncherUrl =
            $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId={browserTrackerId}&placeId={placeId}&isPlayTogetherGame=false";
        var encodedPlaceLauncherUrl = Uri.EscapeDataString(rawPlaceLauncherUrl);

        var effectiveTemplate = string.IsNullOrWhiteSpace(template) || template.TrimStart().StartsWith("--play", StringComparison.OrdinalIgnoreCase)
            ? "roblox-player:1+launchmode:play+gameinfo:{AuthTicket}+launchtime:{LaunchTime}+placelauncherurl:{EncodedPlaceLauncherUrl}+browsertrackerid:{BrowserTrackerId}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp"
            : template;

        return effectiveTemplate
            .Replace("{AuthTicket}", safeTicket, StringComparison.OrdinalIgnoreCase)
            .Replace("{EncodedPlaceLauncherUrl}", encodedPlaceLauncherUrl, StringComparison.OrdinalIgnoreCase)
            .Replace("{PlaceId}", placeId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{BrowserTrackerId}", browserTrackerId, StringComparison.OrdinalIgnoreCase)
            .Replace("{LaunchTime}", launchTime, StringComparison.OrdinalIgnoreCase);
    }

    private static string PrepareIsolatedDirectory(long userId)
    {
        var dirPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxWorkerLauncher",
            "Instances",
            $"Account_{userId}",
            "Temp");

        Directory.CreateDirectory(dirPath);
        return dirPath;
    }

    private static string GetProxyGroupKey(string? proxyAddress)
    {
        if (string.IsNullOrWhiteSpace(proxyAddress))
            return "DIRECT_LOCAL_IP";

        if (Uri.TryCreate(proxyAddress.Trim(), UriKind.Absolute, out var uri))
            return $"{uri.Host}:{uri.Port}";

        return proxyAddress.Trim();
    }

    #region Windows Job Object

    private void InitializeWindowsJobObject()
    {
        _jobObjectHandle = CreateJobObjectW(nint.Zero, null);
        if (_jobObjectHandle == nint.Zero)
            return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        SetInformationJobObject(
            _jobObjectHandle,
            JobObjectExtendedLimitInformation,
            ref info,
            (uint)length);
    }

    private void AssignProcessToJob(nint processHandle)
    {
        if (_jobObjectHandle != nint.Zero && processHandle != nint.Zero)
        {
            AssignProcessToJobObject(_jobObjectHandle, processHandle);
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        int infoType,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopAllSessions();

        if (_jobObjectHandle != nint.Zero)
        {
            CloseHandle(_jobObjectHandle);
            _jobObjectHandle = nint.Zero;
        }
    }
}
