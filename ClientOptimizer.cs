using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Экстремальный оптимизатор ресурсов клиента Roblox с пользовательскими настройками:
/// - Полное фоновое сворачивание окон (SW_SHOWMINNOACTIVE -> 0% GPU) или увод за экран (OffScreen).
/// - Жёсткое квотирование физической ОЗУ в ядре Windows (SetProcessWorkingSetSizeEx + QUOTA_LIMITS_HARDWS_MAX_ENABLE).
/// - Понижение приоритета страниц памяти (MEMORY_PRIORITY_VERY_LOW) и процессора (EcoQoS + Idle).
/// - Сброс кэша страниц через EmptyWorkingSet.
/// </summary>
public sealed class ClientOptimizer : IDisposable
{
    private readonly RemoteConfigService _configService;
    private readonly string _settingsFilePath;
    private readonly PeriodicTimer _resourceTrimTimer;
    private readonly CancellationTokenSource _cts = new();

    private WorkerOptimizationSettings _settings;
    private Func<IReadOnlyCollection<ActiveGameSession>>? _sessionsProvider;
    private Task? _backgroundLoopTask;
    private bool _isDisposed;

    // FPS-лимит применяется только на время запуска окна и затем полностью откатывается.
    private readonly object _fpsSync = new();
    private readonly Dictionary<string, string?> _originalClientSettings = new();
    private int _temporaryFpsUsers;

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    public WorkerOptimizationSettings CurrentSettings => Volatile.Read(ref _settings);

    public ClientOptimizer(RemoteConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _configService.ConfigUpdated += OnRemoteConfigUpdated;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxWorkerLauncher");
        Directory.CreateDirectory(appDataDir);
        _settingsFilePath = Path.Combine(appDataDir, "optimization_settings.json");

        _settings = LoadSettingsFromDisk();

        // Быстрый тик каждые 4 секунды для проверки свёрнутого состояния и жёсткого удержания ОЗУ
        _resourceTrimTimer = new PeriodicTimer(TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Сохраняет новые пользовательские настройки оптимизации и мгновенно применяет их ко всем активным окнам.
    /// </summary>
    public async Task UpdateSettingsAsync(WorkerOptimizationSettings newSettings)
    {
        if (newSettings is null)
            throw new ArgumentNullException(nameof(newSettings));

        newSettings.MaxRamMbPerWindow = Math.Clamp(newSettings.MaxRamMbPerWindow, 80, 600);
        newSettings.TargetFpsCap = Math.Clamp(newSettings.TargetFpsCap, 5, 60);
        newSettings.AntiAfkIntervalMinutes = Math.Clamp(newSettings.AntiAfkIntervalMinutes, 1, 18);

        Volatile.Read(ref _settings);
        Volatile.Write(ref _settings, newSettings);

        try
        {
            var json = JsonSerializer.Serialize(newSettings, PrettyJsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClientOptimizer] Ошибка сохранения настроек оптимизации: {ex.Message}");
        }

        await ApplyOptimizationSettingsAsync().ConfigureAwait(false);

        var sessions = _sessionsProvider?.Invoke();
        if (sessions is not null && sessions.Count > 0)
        {
            ApplyWindowAndMemoryPolicyImmediately(sessions);
        }
    }

    private WorkerOptimizationSettings LoadSettingsFromDisk()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<WorkerOptimizationSettings>(json);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClientOptimizer] Ошибка загрузки настроек оптимизации: {ex.Message}");
        }

        return new WorkerOptimizationSettings();
    }

    public void StartContinuousOptimization(Func<IReadOnlyCollection<ActiveGameSession>> sessionsProvider)
    {
        _sessionsProvider = sessionsProvider ?? throw new ArgumentNullException(nameof(sessionsProvider));
        if (_backgroundLoopTask is not null)
            return;

        _backgroundLoopTask = Task.Run(() => RunContinuousOptimizationLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Считает суммарное потребление физической ОЗУ (Working Set) всеми запущенными окнами Roblox в МБ.
    /// </summary>
    public double GetTotalRobloxRamUsageMb(IReadOnlyCollection<ActiveGameSession> sessions)
    {
        if (sessions is null || sessions.Count == 0)
            return 0;

        long totalBytes = 0;
        foreach (var session in sessions)
        {
            try
            {
                using var proc = Process.GetProcessById(session.ProcessId);
                if (!proc.HasExited)
                {
                    totalBytes += proc.WorkingSet64;
                }
            }
            catch
            {
                // Игнорируем закрывшиеся процессы
            }
        }

        return Math.Round(totalBytes / (1024.0 * 1024.0), 1);
    }

    /// <summary>
    /// Принудительно сжимает ОЗУ всех запущенных окон прямо сейчас и возвращает объём до и после (в МБ).
    /// </summary>
    public (double BeforeMb, double AfterMb) ForceTrimAllSessionsMemory(IReadOnlyCollection<ActiveGameSession> sessions)
    {
        var before = GetTotalRobloxRamUsageMb(sessions);

        foreach (var session in sessions)
        {
            TrimSingleProcessMemory(session.ProcessId);
            session.LastRamTrimUtc = DateTime.UtcNow;
        }

        var after = GetTotalRobloxRamUsageMb(sessions);
        return (before, after);
    }

    /// <summary>
    /// Применяет жёсткий лимит Working Set ядра Windows и сбрасывает кэш страниц для одного процесса по его PID.
    /// </summary>
    public void TrimSingleProcessMemory(int processId)
    {
        var settings = CurrentSettings;
        if (settings.RamMode == RamCompressionLevel.Disabled)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return;

            // Понижаем приоритет страниц памяти процесса до самого низкого (MEMORY_PRIORITY_VERY_LOW = 1)
            SetVeryLowMemoryPriority(process.Handle);

            if (settings.RamMode is RamCompressionLevel.Extreme or RamCompressionLevel.Balanced)
            {
                nint minBytes = (nint)(16L * 1024L * 1024L);
                nint maxBytes = (nint)((long)settings.MaxRamMbPerWindow * 1024L * 1024L);

                // Принудительно ограничиваем физический Working Set на уровне ядра ОС
                SetProcessWorkingSetSizeEx(
                    process.Handle,
                    minBytes,
                    maxBytes,
                    QUOTA_LIMITS_HARDWS_MAX_ENABLE | QUOTA_LIMITS_HARDWS_MIN_DISABLE);
            }

            // Выгружаем все неиспользуемые страницы из ОЗУ
            EmptyWorkingSet(process.Handle);
        }
        catch
        {
            // Игнорируем, если процесс завершился
        }
    }

    public string? FindLatestRobloxExecutablePath()
    {
        // Путь ищется заново при каждом вызове: после обновления Roblox
        // папка version-* меняется, поэтому кэшировать её нельзя.
        var resolution = RobloxPathResolver.Resolve();
        return resolution.HasPlayer
            ? resolution.PlayerExecutablePath
            : RobloxPathResolver.FindLatestPlayerExecutable();
    }

    private static List<FileInfo> FindAllRobloxExecutableFiles()
    {
        var candidateRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions")
        };

        var foundExecutables = new List<FileInfo>();

        foreach (var root in candidateRoots.Where(Directory.Exists))
        {
            try
            {
                var versionDirs = Directory.GetDirectories(root, "version-*", SearchOption.TopDirectoryOnly);
                foreach (var dir in versionDirs)
                {
                    var exePath = Path.Combine(dir, "RobloxPlayerBeta.exe");
                    if (File.Exists(exePath))
                    {
                        foundExecutables.Add(new FileInfo(exePath));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClientOptimizer] Ошибка сканирования папки '{root}': {ex.Message}");
            }
        }

        return foundExecutables;
    }

    public async Task<bool> ApplyOptimizationSettingsAsync()
    {
        bool anyApplied = false;

        // Никогда не храним FPS-лимит в глобальных настройках Roblox.
        if (Volatile.Read(ref _temporaryFpsUsers) == 0)
            RestoreRobloxFpsGlobal();

        try
        {
            await PatchGlobalBasicSettingsXmlAsync().ConfigureAwait(false);
            anyApplied = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClientOptimizer] Ошибка патча GlobalBasicSettings_13.xml: {ex.Message}");
        }

        var settingsDict = new Dictionary<string, object>(_configService.CurrentConfig.ClientAppSettings);

        var executables = FindAllRobloxExecutableFiles();
        if (executables.Count == 0)
            return anyApplied;

        var jsonContent = JsonSerializer.Serialize(settingsDict, PrettyJsonOptions);

        foreach (var exeInfo in executables)
        {
            var versionDirectory = exeInfo.DirectoryName;
            if (string.IsNullOrWhiteSpace(versionDirectory))
                continue;

            try
            {
                var clientSettingsDir = Path.Combine(versionDirectory, "ClientSettings");
                Directory.CreateDirectory(clientSettingsDir);

                var settingsFilePath = Path.Combine(clientSettingsDir, "ClientAppSettings.json");
                await File.WriteAllTextAsync(settingsFilePath, jsonContent).ConfigureAwait(false);
                anyApplied = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClientOptimizer] Не удалось записать ClientAppSettings.json: {ex.Message}");
            }
        }

        return anyApplied;
    }

    /// <summary>
    /// Полностью убирает FPS-лимит из глобальных настроек Roblox.
    /// Вызывается при старте и закрытии лаунчера.
    /// </summary>
    public void RestoreRobloxFpsGlobal()
    {
        lock (_fpsSync)
        {
            if (_temporaryFpsUsers > 0)
                return;

            foreach (var exeInfo in FindAllRobloxExecutableFiles())
            {
                var versionDirectory = exeInfo.DirectoryName;
                if (string.IsNullOrWhiteSpace(versionDirectory)) continue;

                var settingsPath = Path.Combine(versionDirectory, "ClientSettings", "ClientAppSettings.json");
                try
                {
                    if (!File.Exists(settingsPath)) continue;
                    var json = File.ReadAllText(settingsPath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (settings is null || !settings.Remove("DFIntTaskSchedulerTargetFps")) continue;

                    if (settings.Count == 0)
                        File.Delete(settingsPath);
                    else
                        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, PrettyJsonOptions));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClientOptimizer] Не удалось очистить FPS из ClientAppSettings.json: {ex.Message}");
                }
            }

            RestoreGlobalBasicFps();
        }
    }

    /// <summary>
    /// Временно включает FPS-лимит только на момент старта окна Roblox.
    /// Исходные файлы запоминаются и восстанавливаются после старта/закрытия сессии.
    /// </summary>
    public void BeginTemporaryFpsLimit(int targetFps)
    {
        lock (_fpsSync)
        {
            if (_temporaryFpsUsers == 0)
            {
                _originalClientSettings.Clear();
                foreach (var exeInfo in FindAllRobloxExecutableFiles())
                {
                    var versionDirectory = exeInfo.DirectoryName;
                    if (string.IsNullOrWhiteSpace(versionDirectory)) continue;

                    var settingsPath = Path.Combine(versionDirectory, "ClientSettings", "ClientAppSettings.json");
                    string? original = null;
                    try
                    {
                        if (File.Exists(settingsPath))
                        {
                            original = File.ReadAllText(settingsPath);
                            // Не возвращаем старый глобальный FPS-лимит обратно.
                            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(original);
                            if (parsed is not null && parsed.Remove("DFIntTaskSchedulerTargetFps"))
                                original = JsonSerializer.Serialize(parsed, PrettyJsonOptions);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ClientOptimizer] Не удалось сохранить исходные настройки клиента: {ex.Message}");
                    }
                    _originalClientSettings[settingsPath] = original;
                }
            }

            _temporaryFpsUsers += 1;
            var cappedFps = Math.Clamp(targetFps, 5, 240);

            foreach (var pair in _originalClientSettings)
            {
                try
                {
                    var settings = string.IsNullOrWhiteSpace(pair.Value)
                        ? new Dictionary<string, object>()
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(pair.Value) ?? new();
                    settings["DFIntTaskSchedulerTargetFps"] = cappedFps;
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key) ?? string.Empty);
                    File.WriteAllText(pair.Key, JsonSerializer.Serialize(settings, PrettyJsonOptions));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClientOptimizer] Не удалось временно применить FPS-лимит: {ex.Message}");
                }
            }
        }
    }

    public void EndTemporaryFpsLimit()
    {
        lock (_fpsSync)
        {
            if (_temporaryFpsUsers <= 0)
                return;

            _temporaryFpsUsers -= 1;
            if (_temporaryFpsUsers == 0)
                RestoreOriginalClientSettingsLocked();
        }
    }

    /// <summary>
    /// Принудительный откат FPS-лимита (закрытие задания/выход из лаунчера).
    /// </summary>
    public void ForceRestoreTemporaryFps()
    {
        lock (_fpsSync)
        {
            _temporaryFpsUsers = 0;
            RestoreOriginalClientSettingsLocked();
        }
    }

    private void RestoreOriginalClientSettingsLocked()
    {
        foreach (var pair in _originalClientSettings)
        {
            try
            {
                if (pair.Value is null)
                {
                    if (File.Exists(pair.Key)) File.Delete(pair.Key);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Key) ?? string.Empty);
                    File.WriteAllText(pair.Key, pair.Value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClientOptimizer] Не удалось восстановить ClientAppSettings.json: {ex.Message}");
            }
        }

        _originalClientSettings.Clear();
    }

    private static void RestoreGlobalBasicFps()
    {
        try
        {
            var xmlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Roblox",
                "GlobalBasicSettings_13.xml");

            if (!File.Exists(xmlPath)) return;

            var xml = File.ReadAllText(xmlPath);
            var match = Regex.Match(xml, @"<int\s+name=""FramerateCap"">(?<fps>\d+)</int>", RegexOptions.IgnoreCase);
            if (!match.Success) return;

            if (!int.TryParse(match.Groups["fps"].Value, out var fps) || fps > 10)
                return;

            xml = ReplaceXmlTagValue(xml, "int", "FramerateCap", "60");
            File.WriteAllText(xmlPath, xml);
            Debug.WriteLine("[ClientOptimizer] Глобальный FramerateCap восстановлен на 60 FPS.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClientOptimizer] Не удалось восстановить GlobalBasicSettings_13.xml: {ex.Message}");
        }
    }

    private static async Task PatchGlobalBasicSettingsXmlAsync()
    {
        var robloxAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox");

        Directory.CreateDirectory(robloxAppDataDir);
        var xmlPath = Path.Combine(robloxAppDataDir, "GlobalBasicSettings_13.xml");

        if (!File.Exists(xmlPath))
            return;

        var attributes = File.GetAttributes(xmlPath);
        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            File.SetAttributes(xmlPath, attributes & ~FileAttributes.ReadOnly);
        }

        var xml = await File.ReadAllTextAsync(xmlPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xml))
            return;

        xml = ReplaceXmlTagValue(xml, "float", "MasterVolume", "0");
        xml = ReplaceXmlTagValue(xml, "token", "SavedQualityLevel", "1");
        xml = ReplaceXmlTagValue(xml, "int", "GraphicsQualityLevel", "1");
        xml = ReplaceXmlTagValue(xml, "bool", "Fullscreen", "false");
        xml = ReplaceXmlTagValue(xml, "bool", "ReducedMotion", "true");

        await File.WriteAllTextAsync(xmlPath, xml).ConfigureAwait(false);
    }

    private static string ReplaceXmlTagValue(string xml, string tagType, string propName, string newValue)
    {
        var pattern = $@"(<{tagType}\s+name=""{Regex.Escape(propName)}"">)[^<]*(</{tagType}>)";
        return Regex.Replace(xml, pattern, $"${{1}}{newValue}${{2}}", RegexOptions.IgnoreCase);
    }

    private async Task RunContinuousOptimizationLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _resourceTrimTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var sessions = _sessionsProvider?.Invoke();
                if (sessions is null || sessions.Count == 0)
                    continue;

                ApplyWindowAndMemoryPolicyImmediately(sessions);
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClientOptimizer] Ошибка в цикле оптимизации: {ex.Message}");
        }
    }

    private void ApplyWindowAndMemoryPolicyImmediately(IReadOnlyCollection<ActiveGameSession> sessions)
    {
        var settings = CurrentSettings;
        var now = DateTime.UtcNow;

        int index = 0;
        foreach (var session in sessions)
        {
            OptimizeSingleSessionProcess(session, index, settings, now);
            index++;
        }
    }

    private void OptimizeSingleSessionProcess(
        ActiveGameSession session,
        int windowIndex,
        WorkerOptimizationSettings settings,
        DateTime nowUtc)
    {
        // Если прямо сейчас Anti-AFK разбудил окно на 0.6 сек для прыжка — не мешаем ему!
        if (session.IsAntiAfkWakeupInProgress)
            return;

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited)
                return;

            // 1. Управление приоритетом CPU и режимом эффективности Windows (EcoQoS)
            try
            {
                var desiredPriority = settings.EnableEcoQosAndIdlePriority
                    ? ProcessPriorityClass.Idle
                    : ProcessPriorityClass.BelowNormal;

                if (process.PriorityClass != desiredPriority)
                {
                    process.PriorityClass = desiredPriority;
                }
            }
            catch
            {
                // Игнорируем
            }

            if (settings.EnableEcoQosAndIdlePriority)
            {
                EnableProcessEcoQoS(process.Handle);
            }

            // 2. Через 7 секунд после запуска окна (когда игра подключилась к серверу) применяем режим окна (сворачивание / мини-плитка)
            if ((nowUtc - session.StartedAtUtc).TotalSeconds >= 7)
            {
                process.Refresh();
                var hwnd = session.MainWindowHandle != nint.Zero && IsWindow(session.MainWindowHandle)
                    ? session.MainWindowHandle
                    : process.MainWindowHandle;

                if (hwnd != nint.Zero)
                {
                    session.MainWindowHandle = hwnd;
                    ApplyWindowModeToHandle(hwnd, windowIndex, settings.WindowMode);
                }
            }

            // 3. Сжатие оперативной памяти по выбранному интервалу
            if (settings.RamMode != RamCompressionLevel.Disabled)
            {
                var trimIntervalSec = settings.RamMode switch
                {
                    RamCompressionLevel.Extreme => 5,
                    RamCompressionLevel.Balanced => 15,
                    RamCompressionLevel.Light => 30,
                    _ => 15
                };

                if ((nowUtc - session.LastRamTrimUtc).TotalSeconds >= trimIntervalSec)
                {
                    TrimSingleProcessMemory(session.ProcessId);
                    session.LastRamTrimUtc = nowUtc;
                }
            }
        }
        catch
        {
            // Процесс закрылся
        }
    }

    /// <summary>
    /// Применяет выбранный пользователем режим окна (Полное сворачивание, За экраном, Мини-плитка или Обычное).
    /// </summary>
    public static void ApplyWindowModeToHandle(nint hwnd, int windowIndex, RobloxWindowMode mode)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd))
            return;

        switch (mode)
        {
            case RobloxWindowMode.Minimized:
                // Полное сворачивание без кражи фокуса (DirectX прекращает рендер -> 0% GPU)
                if (!IsIconic(hwnd))
                {
                    ShowWindowAsync(hwnd, SW_SHOWMINNOACTIVE);
                }
                break;

            case RobloxWindowMode.OffScreen:
                if (IsIconic(hwnd))
                {
                    ShowWindowAsync(hwnd, SW_SHOWNOACTIVATE);
                }
                SetWindowPos(hwnd, HWND_BOTTOM, -3200, -3200, 180, 140, SWP_NOACTIVATE);
                break;

            case RobloxWindowMode.MicroTiled:
                if (IsIconic(hwnd))
                {
                    ShowWindowAsync(hwnd, SW_SHOWNOACTIVATE);
                }
                if (GetWindowRect(hwnd, out var rect))
                {
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w > 240 || h > 190 || rect.Left < -1000)
                    {
                        int targetX = 16 + (windowIndex % 6) * 205;
                        int targetY = 16 + (windowIndex / 6) * 155;
                        SetWindowPos(hwnd, HWND_BOTTOM, targetX, targetY, 200, 150, SWP_NOACTIVATE);
                    }
                }
                break;

            case RobloxWindowMode.Normal:
                if (IsIconic(hwnd))
                {
                    ShowWindowAsync(hwnd, SW_SHOWNOACTIVATE);
                }
                if (GetWindowRect(hwnd, out var normRect))
                {
                    int w = normRect.Right - normRect.Left;
                    if (w < 400 || normRect.Left < -1000)
                    {
                        SetWindowPos(hwnd, nint.Zero, 100 + (windowIndex * 30), 100 + (windowIndex * 30), 800, 600, SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                }
                break;
        }
    }

    private static void EnableProcessEcoQoS(nint processHandle)
    {
        try
        {
            var throttleState = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            };

            SetProcessInformation(
                processHandle,
                ProcessPowerThrottling,
                ref throttleState,
                Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // Игнорируем
        }
    }

    private static void SetVeryLowMemoryPriority(nint processHandle)
    {
        try
        {
            var memPriority = new MEMORY_PRIORITY_INFORMATION
            {
                MemoryPriority = MEMORY_PRIORITY_VERY_LOW
            };

            SetProcessInformation(
                processHandle,
                ProcessMemoryPriority,
                ref memPriority,
                Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
        }
        catch
        {
            // Игнорируем
        }
    }

    private void OnRemoteConfigUpdated(RemoteConfig newConfig)
    {
        _ = Task.Run(ApplyOptimizationSettingsAsync);
    }

    #region Win32 Kernel & User32 API

    private const int ProcessMemoryPriority = 1;
    private const int ProcessPowerThrottling = 4;
    private const uint MEMORY_PRIORITY_VERY_LOW = 1;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
    private const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x00000004;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;

    private static readonly nint HWND_BOTTOM = 1;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        nint hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        nint hProcess,
        int processInformationClass,
        ref MEMORY_PRIORITY_INFORMATION processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(
        nint hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize,
        uint flags);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint hProcess);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        ForceRestoreTemporaryFps();
        RestoreRobloxFpsGlobal();
        _configService.ConfigUpdated -= OnRemoteConfigUpdated;
        _cts.Cancel();
        _resourceTrimTimer.Dispose();
        _cts.Dispose();
    }
}