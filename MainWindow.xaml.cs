using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RobloxWorker.Launcher.Models;
using RobloxWorker.Launcher.Services;
using Forms = System.Windows.Forms;

namespace RobloxWorker.Launcher;

public partial class MainWindow : Window
{
    private readonly UpdateManagerService _updateManager;
    private readonly RemoteConfigService _configService;
    private readonly MutexManager _mutexManager;
    private readonly ClientOptimizer _clientOptimizer;
    private readonly RobloxAuthService _authService;
    private readonly ProcessLauncher _processLauncher;
    private readonly AntiAfkService _antiAfkService;
    private readonly LogWatcherService _logWatcherService;
    private readonly WorkerWsClient _wsClient;
    private readonly DispatcherTimer _uiRefreshTimer;
    private readonly string _accountsVaultFilePath;
    private readonly string _launcherLogFilePath;

    private readonly LauncherConfigService _localConfigService;
    private readonly LauncherLoginService _loginService = new();
    private readonly LauncherUpdateService _updateService = new();

    private readonly List<WorkerAccount> _savedAccounts = new();
    private readonly HashSet<long> _expiredAccountIds = new();
    private readonly ObservableCollection<AccountRowViewModel> _accountRows = new();
    private readonly ObservableCollection<ActiveCardViewModel> _activeCards = new();

    private LauncherLocalConfig _localConfig;
    private Forms.NotifyIcon? _trayIcon;
    private DispatcherTimer? _updateCheckTimer;
    private DispatcherTimer? _autoFlowTimer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private LauncherVersionInfo? _pendingUpdate;
    private bool _isAutoFlowRequesting;
    private bool _mandatoryUpdateStarted;

    private CancellationTokenSource? _launchLoopCts;
    private bool _isLaunching;
    private bool _isPromptingManualClose;
    private bool _isUiInitializing = true;
    private bool _isLoggedIn;
    private bool _isClosingForReal;
    private bool _isUpdateApplying;
    private bool _wasContractActive;
    private int _orderTargetSeconds;
    private long _modalAccountId;

    public MainWindow()
    {
        InitializeComponent();

        _localConfigService = new LauncherConfigService();
        _localConfig = _localConfigService.Load();
        ThemeManager.Apply(_localConfig.Theme);

        AccountsGrid.ItemsSource = _accountRows;
        ActiveSessionsItems.ItemsSource = _activeCards;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxWorkerLauncher");
        Directory.CreateDirectory(appDataDir);
        _accountsVaultFilePath = Path.Combine(appDataDir, "accounts_vault.json");

        // Логи пишем в logs/launcher.log рядом с программой, а если папка
        // недоступна (Program Files) — в %LOCALAPPDATA%\RobloxWorker\logs.
        var preferredLogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(preferredLogDirectory);
            _launcherLogFilePath = Path.Combine(preferredLogDirectory, "launcher.log");
        }
        catch
        {
            var fallbackLogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobloxWorker",
                "logs");
            Directory.CreateDirectory(fallbackLogDirectory);
            _launcherLogFilePath = Path.Combine(fallbackLogDirectory, "launcher.log");
        }

        _updateManager = new UpdateManagerService();
        _configService = new RemoteConfigService(_updateManager, _localConfig.ServerUrl);
        _mutexManager = new MutexManager(_configService);
        _clientOptimizer = new ClientOptimizer(_configService);
        _authService = new RobloxAuthService();
        _processLauncher = new ProcessLauncher(_configService, _mutexManager, _clientOptimizer, _authService);
        _antiAfkService = new AntiAfkService(_processLauncher, _configService, _clientOptimizer);
        _logWatcherService = new LogWatcherService(_processLauncher, _configService, _authService);
        _wsClient = new WorkerWsClient(_configService, _updateManager, _processLauncher);

        _wsClient.SetTotalAccountsProvider(() => _savedAccounts.Count);

        _processLauncher.SessionStarted += s => AppendLog($"[Launch] Окно PID {s.ProcessId} запущено для {s.Account.Username} (PlaceId: {s.PlaceId})");
        _processLauncher.ContractStateChanged += OnContractStateChanged;
        _processLauncher.ContractViolated += OnContractEarlyExit;

        _antiAfkService.AntiAfkPulseSent += (s, actions) =>
        {
            var modeName = _clientOptimizer.CurrentSettings.GetWindowModeDisplayName();
            AppendLog($"[Anti-AFK] Выполнена цепочка [{actions}] для {s.Account.Username} (PID: {s.ProcessId} | Режим окна: {modeName})");
        };
        _logWatcherService.ServerJobIdDetected += (s, jobId) => AppendLog($"[LogWatcher] Аккаунт {s.Account.Username} вошёл на сервер! JobId: {jobId}");
        _logWatcherService.AntiFraudVerified += (s, info) => AppendLog($"[Анти-Фрод] ✅ Сессия {s.Account.Username} подтверждена: {info}");
        _logWatcherService.SessionDisconnected += (s, code) => AppendLog($"[LogWatcher] Сетевой дисконнект в логах у {s.Account.Username} (Код ошибки: {code}). Запущен авто-реконнект.");
        _logWatcherService.ManualWindowCloseDetected += OnManualWindowCloseDetected;

        _wsClient.ConnectionStatusChanged += OnWsStatusChanged;
        _wsClient.ServerCommandReceived += OnServerCommandReceivedAsync;
        _wsClient.AccountStateReceived += OnAccountStateReceived;

        LoadOptimizationSettingsToUi(_clientOptimizer.CurrentSettings);
        ApplyLocalSettingsToUi();
        _isUiInitializing = false;

        _uiRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiRefreshTimer.Tick += (_, _) =>
        {
            UpdateContractUiState();
            RefreshAccountsGridState();
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"v{_updateManager.CurrentVersion}";
        SettingsVersionText.Text = $"v{_updateManager.CurrentVersion}";
        FooterStatusText.Text = "Готов к работе";
        AppendLog($"[Система] Инициализация лаунчера v{_updateManager.CurrentVersion}...");

        var mutexOk = _mutexManager.AcquireSingletonMutex();
        MutexStatusText.Text = mutexOk
            ? $"Мьютекс ({_configService.CurrentConfig.MutexName}): АКТИВЕН"
            : "Мьютекс: ОШИБКА (закройте Roblox)";

        AppendLog(mutexOk
            ? $"[MutexManager] Мьютекс '{_configService.CurrentConfig.MutexName}' готов к мультиоконному запуску."
            : "[MutexManager] ВНИМАНИЕ: Не удалось захватить мьютекс. Закройте все открытые окна Roblox.");

        LoadSavedAccountsFromDisk();

        var s = _clientOptimizer.CurrentSettings;
        var optimized = await _clientOptimizer.ApplyOptimizationSettingsAsync();
        AppendLog(optimized
            ? $"[ClientOptimizer] Оптимизация активна: Окна='{s.GetWindowModeDisplayName()}', Лимит ОЗУ={s.MaxRamMbPerWindow} МБ, Цепочка AFK=[{s.GetActiveActionsSummary()}]."
            : "[ClientOptimizer] Клиент Roblox пока не найден или настройки будут применены перед стартом.");

        // Убираем возможный старый глобальный FPS-лимит, который мог остаться
        // от предыдущих версий лаунчера.
        await Task.Run(() => _clientOptimizer.RestoreRobloxFpsGlobal());
        _clientOptimizer.StartContinuousOptimization(() => _processLauncher.GetActiveSessions());
        _antiAfkService.Start();
        _logWatcherService.Start();
        _uiRefreshTimer.Start();

        _ = _configService.InitializeAndStartLoopAsync();
        UpdateContractUiState();
        RefreshActiveCards();

        // Автоподключение, если ключ уже сохранён или лежал рядом с .exe.
        if (!string.IsNullOrWhiteSpace(_localConfig.ApiToken))
        {
            ConnectToServer(_localConfig.ApiToken);
        }
        else
        {
            LoginWebsiteBtn.Visibility = Visibility.Visible;
            ChangeAccountBtn.Visibility = Visibility.Collapsed;
            ProfileConnectionText.Text = "Войдите через сайт — ключ подставится автоматически";
        }

        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        _updateCheckTimer.Start();
        _ = CheckForUpdatesAsync(manual: false);

        _autoFlowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoFlowTimer.Tick += async (_, _) => await TryAutoFlowAsync();
        _autoFlowTimer.Start();

        AppendLog("[Система] Все службы запущены и готовы к работе.");
    }

    /// <summary>
    /// Вызывается, когда пользователь вручную закрыл одно из окон Roblox во время активного сеанса.
    /// В зависимости от настройки: спрашивает (открыть снова или выйти), открывает автоматически или завершает сеанс с тайм-аутом.
    /// </summary>
    private void OnManualWindowCloseDetected(ActiveGameSession closedSession)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!_processLauncher.IsContractActive)
                return;

            // Защита от ложных срабатываний сразу после старта: клиент Roblox
            // может перезапускать процесс/окно во время загрузки.
            var sessionUptime = DateTime.UtcNow - closedSession.StartedAtUtc;
            if (sessionUptime.TotalSeconds < 45)
            {
                AppendLog($"⏳ [Старт] Окно {closedSession.Account.Username} закрылось во время загрузки клиента — игнорируем как досрочный выход.");
                return;
            }

            var behavior = _clientOptimizer.CurrentSettings.ManualCloseBehavior;

            if (behavior == ManualWindowCloseBehavior.AlwaysReopenAutomatically)
            {
                AppendLog($"🔄 [Авто-возврат] Окно {closedSession.Account.Username} было закрыто вручную. Настройка: автоматический перезапуск окна...");
                await _processLauncher.LaunchGameWindowAsync(closedSession.Account, closedSession.PlaceId);
                UpdateContractUiState();
                RefreshAccountsGridState();
                return;
            }

            if (behavior == ManualWindowCloseBehavior.StopAllWithTimeoutImmediately)
            {
                _processLauncher.TriggerContractViolation(
                    $"Окно {closedSession.Account.Username} было закрыто вручную.",
                    closedSession);
                return;
            }

            // Режим AskUser: спрашиваем пользователя, случайно ли он закрыл окно
            if (_isPromptingManualClose)
                return;

            _isPromptingManualClose = true;
            try
            {
                var nextExitNumber = _processLauncher.CurrentDailyEarlyExits + 1;
                var nextTimeoutSec = nextExitNumber switch
                {
                    1 => 15,
                    2 => 30,
                    3 => 60,
                    4 => 120,
                    _ => 300
                };

                var answer = MessageBox.Show(
                    this,
                    $"Окно аккаунта «{closedSession.Account.Username}» было закрыто.\n\n" +
                    $"Вы закрыли это окно случайно и хотите открыть его снова, чтобы продолжить выполнение задания?\n\n" +
                    $"• Нажмите «Да» — чтобы сразу перезапустить окно {closedSession.Account.Username} и продолжить работу без тайм-аута.\n" +
                    $"• Нажмите «Нет» — если вы не хотите выполнять задание дальше (закроются остальные окна и включится прогрессивный тайм-аут {nextTimeoutSec} сек., выход за сегодня #{nextExitNumber}).\n\n" +
                    $"(Подсказка: в блоке 2 можно включить автоматическое открытие случайно закрытых окон без этого вопроса).",
                    "Окно игры закрыто — Открыть его снова?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.Yes)
                {
                    if (_processLauncher.IsContractActive)
                    {
                        AppendLog($"🔄 [Возврат окна] Перезапускаем случайно закрытое окно {closedSession.Account.Username}...");
                        await _processLauncher.LaunchGameWindowAsync(closedSession.Account, closedSession.PlaceId);
                        UpdateContractUiState();
                        RefreshAccountsGridState();
                    }
                }
                else
                {
                    _processLauncher.TriggerContractViolation(
                        $"Пользователь закрыл окно {closedSession.Account.Username} и отказался продолжать задание.",
                        closedSession);
                }
            }
            finally
            {
                _isPromptingManualClose = false;
            }
        });
    }

    private async Task<bool> ConfirmContractRulesIfNeededAsync(int slotsCount, string actionTitle)
    {
        var currentSettings = _clientOptimizer.CurrentSettings;
        if (currentSettings.HideContractWarningDialog)
            return true;

        var warningDialog = new ContractWarningWindow(
            slotsCount: slotsCount,
            actionTitle: actionTitle,
            currentHidePreference: currentSettings.HideContractWarningDialog,
            isInfoOnlyMode: false)
        {
            Owner = this
        };

        var result = warningDialog.ShowDialog();
        if (result != true)
            return false;

        if (warningDialog.DontShowAgain != currentSettings.HideContractWarningDialog)
        {
            currentSettings.HideContractWarningDialog = warningDialog.DontShowAgain;
            await _clientOptimizer.UpdateSettingsAsync(currentSettings);
            AppendLog(currentSettings.HideContractWarningDialog
                ? "[Правила] Вы отключили показ правил перед стартом (их всегда можно открыть кнопкой '📖 Правила')."
                : "[Правила] Правила будут показываться перед каждым стартом.");
        }

        return true;
    }

    private async void ShowRulesBtn_Click(object sender, RoutedEventArgs e)
    {
        var currentSettings = _clientOptimizer.CurrentSettings;
        var dialog = new ContractWarningWindow(
            slotsCount: Math.Max(1, _savedAccounts.Count),
            actionTitle: "ПРАВИЛА ИСПОЛНИТЕЛЯ",
            currentHidePreference: currentSettings.HideContractWarningDialog,
            isInfoOnlyMode: true)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            if (dialog.DontShowAgain != currentSettings.HideContractWarningDialog)
            {
                currentSettings.HideContractWarningDialog = dialog.DontShowAgain;
                await _clientOptimizer.UpdateSettingsAsync(currentSettings);
                AppendLog(currentSettings.HideContractWarningDialog
                    ? "[Правила] Показ правил перед стартом отключён."
                    : "[Правила] Показ правил перед стартом снова включён.");
            }
        }
    }

    private void OnContractStateChanged(bool isContractActive)
    {
        var justFinished = _wasContractActive && !isContractActive;
        _wasContractActive = isContractActive;

        if (justFinished)
        {
            _clientOptimizer.ForceRestoreTemporaryFps();
            _ = Task.Run(() => _clientOptimizer.RestoreRobloxFpsGlobal());
        }

        if (justFinished && _localConfig.SoundOnComplete)
        {
            try { SystemSounds.Asterisk.Play(); }
            catch { /* звук недоступен */ }
        }

        if (justFinished && _localConfig.AutoFlowEnabled)
            _ = ResumeAutoFlowAfterDelayAsync();

        Dispatcher.InvokeAsync(UpdateContractUiState);
    }

    private void OnContractEarlyExit(string reason, ActiveGameSession? violatedSession)
    {
        CancelPendingLaunchLoop();

        Dispatcher.InvokeAsync(() =>
        {
            int keptPercent = 100 - ProcessLauncher.EarlyExitFeePercent;
            int timeoutSec = _processLauncher.LastAssignedTimeoutSeconds;
            int dailyCount = _processLauncher.CurrentDailyEarlyExits;

            // Дублируем штраф локально: даже без сети отсчёт переживёт перезапуск.
            if (_processLauncher.TimeoutUntilUtc is { } penaltyUntil &&
                penaltyUntil > DateTime.UtcNow)
            {
                _localConfig.PenaltyUntil = new DateTimeOffset(penaltyUntil.ToUniversalTime())
                    .ToUnixTimeMilliseconds();
                _localConfigService.Save(_localConfig);
            }

            AppendLog(
                $"⏳ [Досрочный выход #{dailyCount} за сегодня] {reason} Все остальные окна закрыты. " +
                $"Отработанное время сохранено ({keptPercent}% выплаты, комиссия {ProcessLauncher.EarlyExitFeePercent}%). " +
                $"Назначен прогрессивный тайм-аут: {timeoutSec} сек. (счётчик выходов сбрасывается в конце дня в 00:00).");

            UpdateContractUiState();
            RefreshAccountsGridState();
        });
    }

    private void CancelPendingLaunchLoop()
    {
        try
        {
            _launchLoopCts?.Cancel();
        }
        catch
        {
            // Игнорируем
        }
    }

    private void UpdateContractUiState()
    {
        int activeWindowsCount = _processLauncher.GetActiveSessions().Count;
        bool isSessionBusy = _processLauncher.IsContractActive || _isLaunching || activeWindowsCount > 0;
        bool isInTimeout = !isSessionBusy && _processLauncher.IsInTimeout;
        int remainingSec = _processLauncher.RemainingTimeoutSeconds;
        int dailyExits = _processLauncher.CurrentDailyEarlyExits;

        BrowserLoginBtn.IsEnabled = !isSessionBusy;
        AddAccountBtn.IsEnabled = !isSessionBusy;
        CookieInputBox.IsEnabled = !isSessionBusy;
        ProxyInputBox.IsEnabled = !isSessionBusy;
        RemoveAccountBtn.IsEnabled = !isSessionBusy;

        if (isSessionBusy)
        {
            ContractStatusText.Text = $"🔒 Сеанс активен (Окон: {activeWindowsCount})";
            ContractStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // #38BDF8
            ContractStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199)); // #0284C7
        }
        else if (isInTimeout)
        {
            ContractStatusText.Text = $"⏳ Штраф: {remainingSec} сек (Выходов за день: {dailyExits})";
            ContractStatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // #FBBF24
            ContractStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6)); // #D97706
        }
        else
        {
            ContractStatusText.Text = dailyExits > 0
                ? $"🟢 Готов к заданию (Выходов за день: {dailyExits})"
                : "🟢 Статус: ожидание задания от платформы";
            ContractStatusText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)); // #4ADE80
            ContractStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)); // #475569
        }

        if (!isInTimeout && _localConfig.PenaltyUntil > 0 &&
            _localConfig.PenaltyUntil <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            _localConfig.PenaltyUntil = 0;
            _localConfigService.Save(_localConfig);
        }
    }

    private void LoadOptimizationSettingsToUi(WorkerOptimizationSettings settings)
    {
        WindowModeComboBox.SelectedIndex = (int)settings.WindowMode;
        RamModeComboBox.SelectedIndex = (int)settings.RamMode;
        ManualCloseModeComboBox.SelectedIndex = (int)settings.ManualCloseBehavior;
        RamLimitSlider.Value = settings.MaxRamMbPerWindow;
        RamSliderValueText.Text = $"{settings.MaxRamMbPerWindow} МБ";
        EcoQosCheckBox.IsChecked = settings.EnableEcoQosAndIdlePriority;
        SmartWakeupCheckBox.IsChecked = settings.SmartAntiAfkWakeup;

        AfkJumpCheckBox.IsChecked = settings.AfkActionJump;
        AfkWalkCheckBox.IsChecked = settings.AfkActionWalkForwardBack;
        AfkStrafeCheckBox.IsChecked = settings.AfkActionStrafeLeftRight;
        AfkCameraRotateCheckBox.IsChecked = settings.AfkActionCameraRotate;
        AfkCameraZoomCheckBox.IsChecked = settings.AfkActionCameraZoom;
        AfkMouseClickCheckBox.IsChecked = settings.AfkActionMouseClick;

        SelectComboBoxItemByTag(FpsCapComboBox, settings.TargetFpsCap.ToString(), defaultIndex: 1);
        SelectComboBoxItemByTag(AntiAfkIntervalComboBox, settings.AntiAfkIntervalMinutes.ToString(), defaultIndex: 2);
    }

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tagValue, int defaultIndex)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = defaultIndex;
    }

    private WorkerOptimizationSettings ReadOptimizationSettingsFromUi()
    {
        int fpsCap = 8;
        if (FpsCapComboBox.SelectedItem is ComboBoxItem fpsItem &&
            int.TryParse(fpsItem.Tag?.ToString(), out var parsedFps))
        {
            fpsCap = parsedFps;
        }

        int afkMinutes = 5;
        if (AntiAfkIntervalComboBox.SelectedItem is ComboBoxItem afkItem &&
            int.TryParse(afkItem.Tag?.ToString(), out var parsedAfk))
        {
            afkMinutes = parsedAfk;
        }

        var current = _clientOptimizer.CurrentSettings;

        return new WorkerOptimizationSettings
        {
            WindowMode = (RobloxWindowMode)Math.Clamp(WindowModeComboBox.SelectedIndex, 0, 3),
            RamMode = (RamCompressionLevel)Math.Clamp(RamModeComboBox.SelectedIndex, 0, 3),
            ManualCloseBehavior = (ManualWindowCloseBehavior)Math.Clamp(ManualCloseModeComboBox.SelectedIndex, 0, 2),
            MaxRamMbPerWindow = (int)RamLimitSlider.Value,
            TargetFpsCap = fpsCap,
            AntiAfkIntervalMinutes = afkMinutes,
            EnableEcoQosAndIdlePriority = EcoQosCheckBox.IsChecked == true,
            SmartAntiAfkWakeup = SmartWakeupCheckBox.IsChecked == true,
            HideContractWarningDialog = current.HideContractWarningDialog,
            DailyEarlyExitCount = current.DailyEarlyExitCount,
            LastEarlyExitDate = current.LastEarlyExitDate,
            AfkActionJump = AfkJumpCheckBox.IsChecked == true,
            AfkActionWalkForwardBack = AfkWalkCheckBox.IsChecked == true,
            AfkActionStrafeLeftRight = AfkStrafeCheckBox.IsChecked == true,
            AfkActionCameraRotate = AfkCameraRotateCheckBox.IsChecked == true,
            AfkActionCameraZoom = AfkCameraZoomCheckBox.IsChecked == true,
            AfkActionMouseClick = AfkMouseClickCheckBox.IsChecked == true
        };
    }

    private async void OptimizationControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing)
            return;

        var updated = ReadOptimizationSettingsFromUi();
        await _clientOptimizer.UpdateSettingsAsync(updated);
        RefreshAccountsGridState();
        AppendLog($"[Настройки] Сохранено: Режим окон='{updated.GetWindowModeDisplayName()}', Лимит ОЗУ={updated.MaxRamMbPerWindow} МБ, Цепочка AFK=[{updated.GetActiveActionsSummary()}].");
    }

    private async void RamLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamSliderValueText is not null)
        {
            RamSliderValueText.Text = $"{(int)e.NewValue} МБ";
        }

        if (_isUiInitializing)
            return;

        var updated = ReadOptimizationSettingsFromUi();
        await _clientOptimizer.UpdateSettingsAsync(updated);
        RefreshAccountsGridState();
    }

    private void ForceTrimRamBtn_Click(object sender, RoutedEventArgs e)
    {
        var active = _processLauncher.GetActiveSessions();
        if (active.Count == 0)
        {
            AppendLog("[Оптимизатор] Нет запущенных окон Roblox для сжатия ОЗУ.");
            return;
        }

        var (beforeMb, afterMb) = _clientOptimizer.ForceTrimAllSessionsMemory(active);
        var savedMb = Math.Max(0, Math.Round(beforeMb - afterMb, 1));
        RefreshAccountsGridState();
        AppendLog($"[Оптимизатор] ⚡ Принудительное сжатие ОЗУ выполнено: было {beforeMb} МБ -> стало {afterMb} МБ (освобождено {savedMb} МБ).");
    }

    private async void TestAntiAfkBtn_Click(object sender, RoutedEventArgs e)
    {
        var active = _processLauncher.GetActiveSessions();
        if (active.Count == 0)
        {
            AppendLog("[Anti-AFK] Нет запущенных окон Roblox для проверки цепочки действий.");
            return;
        }

        TestAntiAfkBtn.IsEnabled = false;
        try
        {
            var currentSettings = _clientOptimizer.CurrentSettings;
            var actionsSummary = currentSettings.GetActiveActionsSummary();
            var modeName = currentSettings.GetWindowModeDisplayName();

            AppendLog($"[Anti-AFK] 🦘 Запуск теста цепочки [{actionsSummary}] (Текущий режим окон: {modeName})...");
            int count = await _antiAfkService.TriggerImmediateAntiAfkForAllAsync();
            RefreshAccountsGridState();

            if (currentSettings.WindowMode == RobloxWindowMode.Minimized)
            {
                AppendLog($"[Anti-AFK] Тест завершён для {count} окон: цепочка выполнена, окна свёрнуты обратно в панель задач.");
            }
            else
            {
                AppendLog($"[Anti-AFK] Тест завершён для {count} окон: цепочка выполнена, окна оставлены открытыми ({modeName}).");
            }
        }
        finally
        {
            TestAntiAfkBtn.IsEnabled = true;
        }
    }

    private async void BrowserLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        var proxy = ProxyInputBox.Text.Trim();
        var loginWindow = new BrowserLoginWindow
        {
            Owner = this
        };

        AppendLog("[Браузер] Открыто изолированное окно входа в аккаунт Roblox...");
        var dialogResult = loginWindow.ShowDialog();

        if (dialogResult == true && !string.IsNullOrWhiteSpace(loginWindow.CapturedRobloSecurity))
        {
            await RegisterCookieAndSaveAccountAsync(loginWindow.CapturedRobloSecurity, proxy);
        }
        else
        {
            AppendLog("[Браузер] Окно входа закрыто без добавления аккаунта.");
        }
    }

    private async void AddAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var rawCookie = CookieInputBox.Password;
        var proxy = ProxyInputBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            MessageBox.Show("Вставьте куку .ROBLOSECURITY или воспользуйтесь кнопкой входа через браузер выше.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddAccountBtn.IsEnabled = false;
        try
        {
            await RegisterCookieAndSaveAccountAsync(rawCookie, proxy);
            CookieInputBox.Clear();
        }
        finally
        {
            UpdateContractUiState();
        }
    }

    private async Task RegisterCookieAndSaveAccountAsync(string rawCookie, string? proxy)
    {
        AppendLog("[Auth] Проверка авторизации и настройка приватности через официальный API Roblox...");

        var account = await _authService.ValidateAndCreateAccountAsync(
            rawCookie,
            string.IsNullOrWhiteSpace(proxy) ? null : proxy);

        if (account is null)
        {
            AppendLog("[Auth] ОШИБКА: Сессия куки недействительна или прокси не отвечает.");
            MessageBox.Show("Не удалось авторизоваться. Проверьте работоспособность куки и прокси.", "Ошибка авторизации", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _savedAccounts.RemoveAll(a => a.UserId == account.UserId);
        _expiredAccountIds.Remove(account.UserId);
        _savedAccounts.Add(account);
        SaveAccountsToDisk();

        RefreshAccountsGridState();
        var privacyText = account.IsPrivacyConfigured ? "Приватность открыта (AllUsers)" : "Приватность по умолчанию";
        AppendLog($"[Auth] УСПЕХ! Аккаунт {account.Username} (UserId: {account.UserId}) сохранён (DPAPI). [{privacyText}]");
    }

    private async Task StartAccountsBatchAsync(
        List<WorkerAccount> accountsToLaunch,
        long placeId,
        string? orderId,
        string orderMode)
    {
        if (_processLauncher.IsInTimeout)
        {
            AppendLog($"⏳ [Штраф] Запуск заблокирован штрафным тайм-аутом: осталось {_processLauncher.RemainingTimeoutSeconds} сек.");
            FooterStatusText.Text = $"Штраф за досрочный выход: {_processLauncher.RemainingTimeoutSeconds} сек.";
            UpdateContractUiState();
            return;
        }

        CancelPendingLaunchLoop();
        _launchLoopCts?.Dispose();
        _launchLoopCts = new CancellationTokenSource();
        var ct = _launchLoopCts.Token;

        _isLaunching = true;
        _processLauncher.StartContract(
            lockedSlotsCount: accountsToLaunch.Count,
            orderId: orderId,
            orderMode: orderMode);

        UpdateContractUiState();
        AppendLog($"[Сеанс] Запущен сеанс ({orderMode}) на {accountsToLaunch.Count} ок. Если нужно срочно выйти — нажмите «⏹ Остановить все окна Roblox».");

        int successLaunched = 0;

        try
        {
            foreach (var account in accountsToLaunch)
            {
                if (ct.IsCancellationRequested || !_processLauncher.IsContractActive)
                    break;

                AppendLog($"[Launch] Получение AuthTicket и запуск окна для {account.Username}...");
                var result = await _processLauncher.LaunchGameWindowAsync(account, placeId, ct);

                if (ct.IsCancellationRequested || !_processLauncher.IsContractActive)
                    break;

                if (!result.IsSuccess)
                {
                    if (result.Message.Contains("9002", StringComparison.OrdinalIgnoreCase) ||
                        result.Message.Contains("401", StringComparison.OrdinalIgnoreCase))
                    {
                        _expiredAccountIds.Add(account.UserId);
                        AppendLog($"[Launch] ВНИМАНИЕ: У аккаунта {account.Username} устарела сессия. Обновите его через кнопку браузера.");
                    }
                    else
                    {
                        AppendLog($"[Launch] Не удалось запустить {account.Username}: {result.Message}");
                    }
                }
                else
                {
                    successLaunched++;
                    _expiredAccountIds.Remove(account.UserId);
                }

                UpdateContractUiState();
                RefreshAccountsGridState();
                await Task.Delay(2500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Очередь запуска прервана
        }
        finally
        {
            _isLaunching = false;

            if (successLaunched == 0 && _processLauncher.GetActiveSessions().Count == 0 && _processLauncher.IsContractActive)
            {
                _processLauncher.CompleteContractNormally();
                AppendLog("[Сеанс] Ни одно окно не удалось запустить. Сеанс завершён без тайм-аута.");
            }

            UpdateContractUiState();
            RefreshAccountsGridState();
        }
    }

    private void StopAllBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingLaunchLoop();
        _isLaunching = false;

        if (_processLauncher.IsContractActive || _processLauncher.GetActiveSessions().Count > 0)
        {
            _processLauncher.TriggerContractViolation("Вы нажали кнопку «Остановить все окна Roblox».");
            return;
        }

        _processLauncher.StopAllSessions();
        UpdateContractUiState();
        RefreshAccountsGridState();
        AppendLog("[Система] Все активные окна Roblox остановлены, файлы сессии освобождены.");
    }

    private void RemoveSelectedAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isLaunching || _processLauncher.IsContractActive || _processLauncher.GetActiveSessions().Count > 0)
            return;

        if (AccountsGrid.SelectedItem is not AccountRowViewModel selected)
            return;

        _processLauncher.StopSession(selected.UserId);
        _savedAccounts.RemoveAll(a => a.UserId == selected.UserId);
        _expiredAccountIds.Remove(selected.UserId);
        SaveAccountsToDisk();
        RefreshAccountsGridState();
        AppendLog($"[Система] Аккаунт {selected.Username} ({selected.UserId}) удалён из лаунчера.");
    }

    // ============================================================
    // ОКНО НАСТРОЙКИ АККАУНТА (ДВОЙНОЙ КЛИК)
    // ============================================================
    private void AccountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not AccountRowViewModel selected)
            return;

        var account = _savedAccounts.FirstOrDefault((item) => item.UserId == selected.UserId);
        if (account is null)
            return;

        _modalAccountId = account.UserId;
        AccModalUsername.Text = account.Username;
        AccModalAvatar.Text = string.IsNullOrWhiteSpace(account.Username)
            ? "?"
            : account.Username.Substring(0, 1).ToUpperInvariant();
        AccModalUserId.Text = $"ID: {account.UserId} · " +
                              (string.IsNullOrWhiteSpace(account.ProxyAddress) ? "Прямой IP" : account.ProxyAddress);
        AccModalNote.Text = account.Note ?? string.Empty;
        UpdateAccountModalStatus(account);
        AccountModalBackdrop.Visibility = Visibility.Visible;
    }

    private void UpdateAccountModalStatus(WorkerAccount account)
    {
        var session = _processLauncher.GetActiveSessions()
            .FirstOrDefault((item) => item.Account.UserId == account.UserId);

        if (session is null)
        {
            AccModalStatus.Text = "Сессия: не запущена. Можно проверить валидность или открыть профиль Roblox.";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(107, 122, 141));
            return;
        }

        if (session.IsFullyVerified)
        {
            AccModalStatus.Text = $"Сессия: в игре · PID {session.ProcessId} · JobId {session.CurrentJobId ?? "—"}";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        else
        {
            AccModalStatus.Text = $"Сессия: подключение к Roblox · PID {session.ProcessId}";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
    }

    private void CloseAccountModal_Click(object sender, RoutedEventArgs e)
    {
        AccountModalBackdrop.Visibility = Visibility.Collapsed;
    }

    private void AccountModalBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            AccountModalBackdrop.Visibility = Visibility.Collapsed;
    }

    private void AccountModalCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private WorkerAccount? GetModalAccount()
        => _savedAccounts.FirstOrDefault((item) => item.UserId == _modalAccountId);

    private void SaveAccountNote_Click(object sender, RoutedEventArgs e)
    {
        var account = GetModalAccount();
        if (account is null) return;

        account.Note = (AccModalNote.Text ?? string.Empty).Trim();
        SaveAccountsToDisk();
        FooterStatusText.Text = "Заметка сохранена";
        AppendLog($"[Аккаунт] Заметка для {account.Username} сохранена.");
    }

    private async void ValidateAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = GetModalAccount();
        if (account is null) return;

        AccModalValidateBtn.IsEnabled = false;
        AccModalStatus.Text = "Проверка сессии Roblox...";
        AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));

        try
        {
            if (string.IsNullOrWhiteSpace(account.EncryptedRobloSecurity))
                throw new InvalidOperationException("В хранилище нет сохранённой куки аккаунта.");

            var rawCookie = RobloxAuthService.UnprotectCookie(account.EncryptedRobloSecurity);
            var fresh = await _authService.ValidateAndCreateAccountAsync(
                rawCookie,
                account.ProxyAddress,
                account.ProxyUsername,
                account.ProxyPassword);

            if (fresh is null)
            {
                _expiredAccountIds.Add(account.UserId);
                AccModalStatus.Text = "Сессия недействительна. Войдите в аккаунт заново.";
                AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                RefreshAccountsGridState();
                return;
            }

            account.Username = fresh.Username;
            account.DisplayName = fresh.DisplayName;
            account.IsPrivacyConfigured = fresh.IsPrivacyConfigured;
            account.EncryptedRobloSecurity = fresh.EncryptedRobloSecurity;
            _expiredAccountIds.Remove(account.UserId);

            SaveAccountsToDisk();
            RefreshAccountsGridState();

            AccModalUsername.Text = account.Username;
            AccModalAvatar.Text = string.IsNullOrWhiteSpace(account.Username)
                ? "?"
                : account.Username.Substring(0, 1).ToUpperInvariant();
            AccModalStatus.Text = "Сессия действительна. Аккаунт готов к запуску.";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            AppendLog($"[Аккаунт] Сессия {account.Username} успешно проверена.");
        }
        catch (Exception ex)
        {
            AccModalStatus.Text = "Ошибка проверки: " + ex.Message;
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            AppendLog($"[Аккаунт] Ошибка проверки {account.Username}: {ex.Message}");
        }
        finally
        {
            AccModalValidateBtn.IsEnabled = true;
        }
    }

    private void DeleteAccountFromModal_Click(object sender, RoutedEventArgs e)
    {
        var account = GetModalAccount();
        if (account is null) return;

        if (_isLaunching || _processLauncher.IsContractActive || _processLauncher.GetActiveSessions().Count > 0)
        {
            AccModalStatus.Text = "Нельзя удалить аккаунт во время активного задания.";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            return;
        }

        _processLauncher.StopSession(account.UserId);
        _savedAccounts.RemoveAll((item) => item.UserId == account.UserId);
        _expiredAccountIds.Remove(account.UserId);
        SaveAccountsToDisk();
        RefreshAccountsGridState();
        AccountModalBackdrop.Visibility = Visibility.Collapsed;
        AppendLog($"[Аккаунт] {account.Username} удалён из списка.");
    }

    private void OpenAccountInRoblox_Click(object sender, RoutedEventArgs e)
    {
        var account = GetModalAccount();
        if (account is null) return;

        try
        {
            // Только профиль Roblox в браузере. Запуск игр возможен строго
            // по команде start_order от платформы.
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{account.UserId}/profile")
            {
                UseShellExecute = true
            });
            AccModalStatus.Text = "Профиль Roblox открыт в браузере.";
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        }
        catch (Exception ex)
        {
            AccModalStatus.Text = "Не удалось открыть профиль: " + ex.Message;
            AccModalStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
    }

    private void OnWsStatusChanged(bool isConnected, string statusMessage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            WsStatusText.Text = $"Платформа: {statusMessage}";
            WsDot.Fill = isConnected
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : new SolidColorBrush(Color.FromRgb(107, 122, 141));

            if (isConnected)
                ProfileConnectionText.Text = "Подключение к платформе установлено · ожидание профиля...";
            else if (_isLoggedIn)
                ProfileConnectionText.Text = "Соединение с платформой потеряно";

            AppendLog($"[Соединение] {statusMessage}");
        });
    }

    private async Task OnServerCommandReceivedAsync(ServerCommandMessage cmd)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (string.Equals(cmd.Command, "start_order", StringComparison.OrdinalIgnoreCase) && cmd.PlaceId > 0)
            {
                // Защита от "круговой поруки": если у воркера сейчас идёт тайм-аут или окна уже в работе — отклоняем авто-старт!
                if (_processLauncher.IsInTimeout)
                {
                    AppendLog($"⏳ [Биржа] Пропуск команды start_order, так как у вас активен тайм-аут ({_processLauncher.RemainingTimeoutSeconds} сек).");
                    return;
                }

                if (_isLaunching || _processLauncher.IsContractActive || _processLauncher.GetActiveSessions().Count > 0)
                {
                    return;
                }

                AppendLog($"[Команда биржи] Получен заказ '{cmd.OrderId}' (Режим: {cmd.OrderMode}, PlaceId: {cmd.PlaceId}, Слотов: {cmd.RequestedAccountsCount})");

                var freeAccounts = _savedAccounts.Take(cmd.RequestedAccountsCount).ToList();

                if (freeAccounts.Count == 0)
                    return;

                await StartAccountsBatchAsync(freeAccounts, cmd.PlaceId, cmd.OrderId, cmd.OrderMode);
            }
            else if (string.Equals(cmd.Command, "stop_order", StringComparison.OrdinalIgnoreCase))
            {
                CancelPendingLaunchLoop();
                _isLaunching = false;
                _processLauncher.CompleteContractNormally();
                UpdateContractUiState();
                RefreshAccountsGridState();
                AppendLog("✅ [Заказ завершён] Платформа штатно завершила заказ! Все окна закрыты, выплата начислена полностью.");
            }
        });
    }

    private void RefreshAccountsGridState()
    {
        var activeSessions = _processLauncher.GetActiveSessions();
        var activeByUserId = activeSessions.ToDictionary(s => s.Account.UserId, s => s);

        ActiveWindowsCountText.Text = $"Активных окон: {activeByUserId.Count}";
        var totalRamMb = _clientOptimizer.GetTotalRobloxRamUsageMb(activeSessions);
        RamUsageText.Text = $"ОЗУ Roblox: {totalRamMb} МБ";

        // Не пересоздаём коллекцию: иначе DataGrid теряет выделение каждые 2 секунды.
        var selectedId = (AccountsGrid.SelectedItem as AccountRowViewModel)?.UserId;
        var savedIds = new HashSet<long>(_savedAccounts.Select((account) => account.UserId));

        for (var rowIndex = _accountRows.Count - 1; rowIndex >= 0; rowIndex -= 1)
        {
            if (!savedIds.Contains(_accountRows[rowIndex].UserId))
                _accountRows.RemoveAt(rowIndex);
        }

        var rowsById = _accountRows.ToDictionary((row) => row.UserId);

        foreach (var acc in _savedAccounts)
        {
            activeByUserId.TryGetValue(acc.UserId, out var session);

            string status;
            if (session is not null)
            {
                status = session.IsFullyVerified
                    ? $"🟢 Подтверждён (PID: {session.ProcessId})"
                    : $"🟡 Вход в игру... (PID: {session.ProcessId})";
            }
            else if (_expiredAccountIds.Contains(acc.UserId))
            {
                status = "🔴 Кука устарела (Перезайдите)";
            }
            else if (_processLauncher.IsInTimeout && activeByUserId.Count == 0)
            {
                status = $"⏳ Тайм-аут ({_processLauncher.RemainingTimeoutSeconds} с)";
            }
            else
            {
                status = "⚪ Ожидание";
            }

            if (!rowsById.TryGetValue(acc.UserId, out var row))
            {
                row = new AccountRowViewModel { UserId = acc.UserId };
                _accountRows.Add(row);
                rowsById[acc.UserId] = row;
            }

            row.Username = acc.Username;
            row.ProxyDisplay = string.IsNullOrWhiteSpace(acc.ProxyAddress) ? "Прямой IP" : acc.ProxyAddress;
            row.StatusText = status;
            row.CurrentJobId = session?.CurrentJobId ?? "—";
        }

        // Восстанавливаем выделение по ID, если DataGrid его сбросил.
        if (selectedId.HasValue && AccountsGrid.SelectedItem is null)
        {
            AccountsGrid.SelectedItem = _accountRows.FirstOrDefault((row) => row.UserId == selectedId.Value);
        }

        RefreshActiveCards();
    }

    private void LoadSavedAccountsFromDisk()
    {
        try
        {
            if (!File.Exists(_accountsVaultFilePath))
                return;

            var json = File.ReadAllText(_accountsVaultFilePath);
            var loaded = JsonSerializer.Deserialize<List<WorkerAccount>>(json);
            if (loaded is not null)
            {
                _savedAccounts.Clear();
                _savedAccounts.AddRange(loaded);
                RefreshAccountsGridState();
                AppendLog($"[Хранилище] Загружено аккаунтов из зашифрованного кэша: {_savedAccounts.Count}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Хранилище] Ошибка чтения кэша аккаунтов: {ex.Message}");
        }
    }

    private void SaveAccountsToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_savedAccounts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_accountsVaultFilePath, json);
        }
        catch (Exception ex)
        {
            AppendLog($"[Хранилище] Ошибка сохранения аккаунтов: {ex.Message}");
        }
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => AppendLog(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        // Логи тихо пишутся в файл; на экран выводятся только в техническом журнале.
        try
        {
            var logFile = new FileInfo(_launcherLogFilePath);
            if (logFile.Exists && logFile.Length > 5 * 1024 * 1024)
                File.WriteAllText(_launcherLogFilePath, string.Empty);
            File.AppendAllText(_launcherLogFilePath, line + Environment.NewLine);
        }
        catch
        {
            /* файл лога недоступен — не мешаем работе лаунчера */
        }

        if (LogConsoleBox is not null)
        {
            LogConsoleBox.AppendText(line + Environment.NewLine);
            LogConsoleBox.ScrollToEnd();
        }
    }

    private void ApplyLocalSettingsToUi()
    {
        ThemeComboBox.SelectedIndex = string.Equals(_localConfig.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MinimizeToTrayCheckBox.IsChecked = _localConfig.MinimizeToTray;
        SoundOnCompleteCheckBox.IsChecked = _localConfig.SoundOnComplete;
        AutoFlowToggle.IsChecked = _localConfig.AutoFlowEnabled;
        SelectComboBoxItemByTag(AutoFlowFilterComboBox, _localConfig.AutoFlowFilter, defaultIndex: 0);
        AutoFlowStatusText.Text = _localConfig.AutoFlowEnabled
            ? "Автопоток включён: ищем подходящее задание..."
            : "Автопоток выключен";
        SettingsVersionText.Text = $"v{_updateManager.CurrentVersion}";
        SettingsUsernameText.Text = "Гость";
        SettingsProfileText.Text = "Аккаунт не подключён";
        SettingsChangeAccountBtn.Visibility = Visibility.Collapsed;
        ChangeAccountBtn.Visibility = Visibility.Collapsed;
        LoginWebsiteBtn.Visibility = Visibility.Visible;

        // Локально сохранённый штраф продолжает тикать сразу после запуска,
        // ещё до ответа сервера.
        if (_localConfig.PenaltyUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            _processLauncher.ApplyServerPenalty(
                DateTimeOffset.FromUnixTimeMilliseconds(_localConfig.PenaltyUntil).UtcDateTime);
        }

        if (_localConfig.MinimizeToTray)
            EnsureTrayIcon();
    }

    // ============================================================
    // ПРОФИЛЬ И КАРТОЧКИ АКТИВНЫХ ОКОН
    // ============================================================
    private void OnAccountStateReceived(AccountStateMessage state)
    {
        Dispatcher.InvokeAsync(() => ApplyAccountState(state));
    }

    private void ApplyAccountState(AccountStateMessage state)
    {
        _isLoggedIn = true;

        if (!string.IsNullOrWhiteSpace(state.ActiveOrderId) && state.TargetSeconds > 0)
            _orderTargetSeconds = state.TargetSeconds;
        else if (string.IsNullOrWhiteSpace(state.ActiveOrderId))
            _orderTargetSeconds = 0;

        var username = string.IsNullOrWhiteSpace(state.Username) ? "Исполнитель" : state.Username.Trim();
        ProfileUsernameText.Text = username;
        ProfileAvatarText.Text = username.Substring(0, 1).ToUpperInvariant();
        ProfileBalanceText.Text = state.MainBalanceRub.ToString("0.00") + " ₽";
        ProfilePendingText.Text = "+" + state.PendingEarningsRub.ToString("0.00") + " ₽";
        ProfileConnectionText.Text = string.IsNullOrWhiteSpace(state.ActiveOrderId)
            ? "Подключено к платформе · задание не запущено"
            : $"Подключено к платформе · заказ {state.ActiveOrderId}";
        SettingsUsernameText.Text = username;
        SettingsProfileText.Text = $"Основной баланс: {state.MainBalanceRub:0.00} ₽ · в процессе: +{state.PendingEarningsRub:0.00} ₽";

        // Аккаунт платформы привязан. Обычный пользователь больше не может
        // сменить его или выйти — только администратор.
        LoginWebsiteBtn.Visibility = Visibility.Collapsed;
        ChangeAccountBtn.Visibility = state.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        SettingsChangeAccountBtn.Visibility = state.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state.PenaltyUntil > 0)
        {
            _processLauncher.ApplyServerPenalty(
                DateTimeOffset.FromUnixTimeMilliseconds(state.PenaltyUntil).UtcDateTime);
            if (_localConfig.PenaltyUntil != state.PenaltyUntil)
            {
                _localConfig.PenaltyUntil = state.PenaltyUntil;
                _localConfigService.Save(_localConfig);
            }
        }
        else if (_localConfig.PenaltyUntil > 0 && _localConfig.PenaltyUntil <= nowMs)
        {
            _localConfig.PenaltyUntil = 0;
            _localConfigService.Save(_localConfig);
        }

        FooterStatusText.Text = "Профиль обновлён в " + DateTime.Now.ToString("HH:mm:ss");
        RefreshActiveCards();
        UpdateContractUiState();

        if (state.UpdateRequired)
        {
            _ = BeginMandatoryUpdateAsync();
            return;
        }

        if (_localConfig.AutoFlowEnabled)
            _ = TryAutoFlowAsync();
    }

    private void RefreshActiveCards()
    {
        var sessions = _processLauncher.GetActiveSessions();
        var activeIds = new HashSet<long>(sessions.Select(s => s.Account.UserId));

        for (var i = _activeCards.Count - 1; i >= 0; i -= 1)
        {
            if (!activeIds.Contains(_activeCards[i].UserId))
                _activeCards.RemoveAt(i);
        }

        var now = DateTime.UtcNow;
        var target = _orderTargetSeconds > 0
            ? TimeSpan.FromSeconds(_orderTargetSeconds)
            : TimeSpan.FromHours(1);

        foreach (var session in sessions)
        {
            var card = _activeCards.FirstOrDefault(c => c.UserId == session.Account.UserId);
            if (card is null)
            {
                card = new ActiveCardViewModel { UserId = session.Account.UserId };
                _activeCards.Add(card);
            }

            var elapsed = now - session.StartedAtUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            card.Username = session.Account.Username;
            card.StatusText = session.IsFullyVerified ? "В игре" : "Подключение";
            card.StatusColor = session.IsFullyVerified
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            card.DetailText = $"PID {session.ProcessId} · Place {session.PlaceId} · " +
                              (string.IsNullOrWhiteSpace(session.Account.ProxyAddress) ? "Прямой IP" : session.Account.ProxyAddress);
            card.TimerText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00} / " +
                             $"{(int)target.TotalHours:00}:{target.Minutes:00}";
            card.ProgressValue = Math.Min(100, elapsed.TotalSeconds / Math.Max(1, target.TotalSeconds) * 100);
            card.JobId = string.IsNullOrWhiteSpace(session.CurrentJobId) ? "—" : session.CurrentJobId;
        }

        EmptySessionsText.Visibility = _activeCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    // ВКЛАДКИ И НАСТРОЙКИ ВНЕШНЕГО ВИДА
    // ============================================================
    private void TabTasksBtn_Click(object sender, RoutedEventArgs e) => SwitchTab("tasks");

    private void TabSettingsBtn_Click(object sender, RoutedEventArgs e) => SwitchTab("settings");

    private void SwitchTab(string tab)
    {
        var isTasks = string.Equals(tab, "tasks", StringComparison.OrdinalIgnoreCase);
        TasksTabPanel.Visibility = isTasks ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabPanel.Visibility = isTasks ? Visibility.Collapsed : Visibility.Visible;
        TabTasksBtn.Tag = isTasks ? "active" : null;
        TabSettingsBtn.Tag = isTasks ? null : "active";
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing || _localConfig is null) return;
        var theme = ThemeComboBox.SelectedIndex == 1 ? "light" : "dark";
        if (string.Equals(_localConfig.Theme, theme, StringComparison.OrdinalIgnoreCase)) return;
        _localConfig.Theme = theme;
        ThemeManager.Apply(theme);
        _localConfigService.Save(_localConfig);
        AppendLog($"[Настройки] Тема переключена на {(theme == "light" ? "светлую" : "тёмную")}.");
    }

    private void TraySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing) return;
        _localConfig.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        _localConfigService.Save(_localConfig);
        if (_localConfig.MinimizeToTray) EnsureTrayIcon();
        else RemoveTrayIcon();
    }

    private void SoundSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing) return;
        _localConfig.SoundOnComplete = SoundOnCompleteCheckBox.IsChecked == true;
        _localConfigService.Save(_localConfig);
    }

    private void ShowLogCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (LogPanel is null) return;
        LogPanel.Visibility = ShowLogCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================
    // АВТОПОТОК
    // ============================================================
    private void AutoFlowToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUiInitializing || _localConfig is null) return;

        _localConfig.AutoFlowEnabled = AutoFlowToggle.IsChecked == true;
        _localConfigService.Save(_localConfig);

        if (_localConfig.AutoFlowEnabled)
        {
            AutoFlowStatusText.Text = "Автопоток включён: запрашиваем задание у платформы...";
            _ = TryAutoFlowAsync();
        }
        else
        {
            AutoFlowStatusText.Text = "Автопоток выключен";
        }
    }

    private void AutoFlowFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isUiInitializing || _localConfig is null) return;

        _localConfig.AutoFlowFilter =
            AutoFlowFilterComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
                ? tag
                : "price";
        _localConfigService.Save(_localConfig);

        if (_localConfig.AutoFlowEnabled)
            _ = TryAutoFlowAsync();
    }

    private async Task TryAutoFlowAsync()
    {
        if (!_localConfig.AutoFlowEnabled || _isAutoFlowRequesting || _mandatoryUpdateStarted)
            return;
        if (!_isLoggedIn || string.IsNullOrWhiteSpace(_localConfig.ApiToken))
            return;
        if (_processLauncher.IsInTimeout)
        {
            AutoFlowStatusText.Text = $"Пауза из-за штрафа: {_processLauncher.RemainingTimeoutSeconds} сек.";
            return;
        }
        if (_isLaunching || _processLauncher.IsContractActive || _processLauncher.GetActiveSessions().Count > 0)
        {
            AutoFlowStatusText.Text = "Текущее задание выполняется.";
            return;
        }
        if (_savedAccounts.Count == 0)
        {
            AutoFlowStatusText.Text = "Добавьте хотя бы один аккаунт Roblox.";
            return;
        }

        _isAutoFlowRequesting = true;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                LauncherConfigService.NormalizeServerUrl(_localConfig.ServerUrl) + "/api/orders/auto-assign");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _localConfig.ApiToken);
            request.Headers.Add("X-Client-Version", _updateManager.CurrentVersion);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { filter = _localConfig.AutoFlowFilter }),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeElement)
                ? (codeElement.GetString() ?? string.Empty)
                : string.Empty;

            if ((int)response.StatusCode == 426 || code == "UPDATE_REQUIRED")
            {
                await BeginMandatoryUpdateAsync();
                return;
            }

            if ((int)response.StatusCode == 429)
            {
                if (root.TryGetProperty("penalty_until", out var untilElement) &&
                    untilElement.TryGetInt64(out var until) && until > 0)
                {
                    _localConfig.PenaltyUntil = until;
                    _localConfigService.Save(_localConfig);
                    _processLauncher.ApplyServerPenalty(
                        DateTimeOffset.FromUnixTimeMilliseconds(until).UtcDateTime);
                }
                var remaining = root.TryGetProperty("penalty_remaining_seconds", out var remElement)
                    ? remElement.GetInt32()
                    : _processLauncher.RemainingTimeoutSeconds;
                AutoFlowStatusText.Text = $"Штраф за досрочный выход: {remaining} сек.";
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                AutoFlowStatusText.Text = "Платформа временно недоступна, повтор через 5 сек.";
                return;
            }

            switch (code)
            {
                case "LAUNCHED_IN_CLIENT":
                {
                    var title = root.TryGetProperty("order", out var orderElement) &&
                                orderElement.TryGetProperty("game_title", out var titleElement)
                        ? (titleElement.GetString() ?? "задание")
                        : "задание";
                    AutoFlowStatusText.Text = $"Задание получено: {title}. Запускаем окна...";
                    break;
                }
                case "NO_ORDERS":
                    AutoFlowStatusText.Text = "Свободных заданий пока нет. Продолжаем поиск...";
                    break;
                case "NO_ACCOUNTS":
                    AutoFlowStatusText.Text = "Добавьте хотя бы один аккаунт Roblox.";
                    break;
                case "BUSY":
                    AutoFlowStatusText.Text = "Текущее задание уже выполняется.";
                    break;
                case "LAUNCHER_NOT_CONNECTED":
                    AutoFlowStatusText.Text = "Подключение к платформе восстанавливается...";
                    break;
                default:
                    AutoFlowStatusText.Text = "Автопоток активен. Ожидание задания...";
                    break;
            }
        }
        catch (Exception ex)
        {
            AutoFlowStatusText.Text = "Нет связи с платформой. Повтор через 5 сек.";
            AppendLog($"[Автопоток] Ошибка запроса: {ex.Message}");
        }
        finally
        {
            _isAutoFlowRequesting = false;
        }
    }

    private async Task ResumeAutoFlowAfterDelayAsync()
    {
        await Task.Delay(1500);
        if (!_localConfig.AutoFlowEnabled) return;
        await Dispatcher.InvokeAsync(() => { _ = TryAutoFlowAsync(); });
    }

    // ============================================================
    // КАСТОМНОЕ ОКНО
    // ============================================================
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch { /* окно уже перемещается */ }
    }

    private void MinimizeWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ============================================================
    // ВХОД ЧЕРЕЗ САЙТ
    // ============================================================
    private async void LoginWebsiteBtn_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = LauncherConfigService.NormalizeServerUrl(_localConfig.ServerUrl);

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        FooterStatusText.Text = "Ожидание входа через сайт...";
        AppendLog($"[Вход] Запуск привязки аккаунта через сайт {baseUrl}...");

        try
        {
            var result = await _loginService.LoginAsync(baseUrl, TimeSpan.FromMinutes(3));
            if (result is null)
            {
                FooterStatusText.Text = "Вход не выполнен";
                MessageBox.Show(
                    this,
                    "Вход не завершён. Откройте страницу сайта в браузере, войдите в аккаунт и попробуйте снова.",
                    "Вход через сайт",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _localConfig.ApiToken = result.Token;
            _localConfig.ServerUrl = baseUrl;
            _localConfigService.Save(_localConfig);

            LoginWebsiteBtn.Visibility = Visibility.Collapsed;
            ChangeAccountBtn.Visibility = Visibility.Collapsed;
            SettingsChangeAccountBtn.Visibility = Visibility.Collapsed;
            ConnectToServer(result.Token);

            if (!string.IsNullOrWhiteSpace(result.Username))
            {
                ProfileUsernameText.Text = result.Username;
                ProfileAvatarText.Text = result.Username.Substring(0, 1).ToUpperInvariant();
                SettingsUsernameText.Text = result.Username;
            }

            FooterStatusText.Text = "Аккаунт подключён";
            AppendLog("[Вход] Ключ получен, лаунчер подключается к платформе.");
        }
        catch (Exception ex)
        {
            AppendLog($"[Вход] Ошибка: {ex.Message}");
            MessageBox.Show(this, "Не удалось выполнить вход: " + ex.Message, "Вход через сайт",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void ConnectToServer(string token)
    {
        var normalized = LauncherConfigService.NormalizeServerUrl(_localConfig.ServerUrl);
        _localConfig.ServerUrl = normalized;
        _localConfig.ApiToken = token;

        _configService.SetApiBaseUrl(normalized);
        _configService.SetWorkerApiToken(token);

        _wsClient.Connect(LauncherConfigService.BuildWorkerWsUrl(normalized), token);
        FooterStatusText.Text = "Подключение к платформе...";
        AppendLog("[Соединение] Устанавливаем защищённое подключение к платформе...");
    }

    // ============================================================
    // АВТООБНОВЛЕНИЕ
    // ============================================================
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_isUpdateApplying) return;

        try
        {
            var baseUrl = LauncherConfigService.NormalizeServerUrl(_localConfig.ServerUrl);
            var info = await _updateService.GetLatestVersionAsync(baseUrl);
            if (info is null)
            {
                if (manual)
                    MessageBox.Show(this, "Не удалось проверить обновления. Проверьте подключение к интернету.",
                        "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!LauncherUpdateService.IsNewer(info.Version, _updateManager.CurrentVersion))
            {
                _pendingUpdate = null;
                UpdateBanner.Visibility = Visibility.Collapsed;
                if (manual)
                    MessageBox.Show(this, $"У вас актуальная версия {_updateManager.CurrentVersion}.",
                        "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _pendingUpdate = info;
            UpdateBanner.Visibility = Visibility.Collapsed;
            FooterStatusText.Text = "Требуется обновление Robust до " + info.Version;
            AppendLog($"[Обновление] Обязательное обновление до версии {info.Version}.");
            await BeginMandatoryUpdateAsync(info);
        }
        catch (Exception ex)
        {
            AppendLog($"[Обновление] Ошибка проверки: {ex.Message}");
        }
    }

    private async Task BeginMandatoryUpdateAsync(LauncherVersionInfo? knownInfo = null)
    {
        if (_mandatoryUpdateStarted)
            return;

        _mandatoryUpdateStarted = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
        MandatoryUpdateOverlay.Visibility = Visibility.Visible;
        MandatoryUpdateRetryBtn.Visibility = Visibility.Collapsed;
        MandatoryUpdateProgress.Visibility = Visibility.Visible;
        MandatoryUpdateProgress.Value = 0;
        MandatoryUpdateStatus.Text = "Проверка актуальной версии Robust...";

        // Блокируем любые действия до успешного обновления.
        LaunchAllButtonsForUpdate();

        await StartMandatoryUpdateDownloadAsync(knownInfo);
    }

    private void LaunchAllButtonsForUpdate()
    {
        AutoFlowToggle.IsEnabled = false;
        BrowserLoginBtn.IsEnabled = false;
        AddAccountBtn.IsEnabled = false;
        LoginWebsiteBtn.IsEnabled = false;
        ChangeAccountBtn.IsEnabled = false;
    }

    private async Task StartMandatoryUpdateDownloadAsync(LauncherVersionInfo? knownInfo = null)
    {
        if (_isUpdateApplying)
            return;

        _isUpdateApplying = true;
        MandatoryUpdateRetryBtn.IsEnabled = false;

        try
        {
            var info = knownInfo ?? await _updateService.GetLatestVersionAsync(
                LauncherConfigService.NormalizeServerUrl(_localConfig.ServerUrl));

            if (info is null)
                throw new InvalidOperationException("Сервер не вернул информацию об обновлении.");

            _pendingUpdate = info;
            MandatoryUpdateProgress.Visibility = Visibility.Visible;
            MandatoryUpdateStatus.Text = $"Загрузка Robust {info.Version}...";

            var progress = new Progress<double>(value =>
            {
                MandatoryUpdateProgress.Value = value;
                MandatoryUpdateStatus.Text = $"Загрузка обновления Robust... {value:0}%";
            });

            await _updateService.DownloadAndApplyAsync(info, progress);
            MandatoryUpdateStatus.Text = "Обновление готово. Перезапуск Robust...";
            AppendLog("[Обновление] Обязательное обновление загружено, перезапускаем лаунчер.");
            await Task.Delay(900);

            _isClosingForReal = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isUpdateApplying = false;
            MandatoryUpdateProgress.Visibility = Visibility.Collapsed;
            MandatoryUpdateRetryBtn.Visibility = Visibility.Visible;
            MandatoryUpdateRetryBtn.IsEnabled = true;
            MandatoryUpdateStatus.Text = "Не удалось обновить Robust. Проверьте интернет и повторите.";
            AppendLog($"[Обновление] Ошибка обязательного обновления: {ex.Message}");
        }
    }

    private async void MandatoryUpdateRetryBtn_Click(object sender, RoutedEventArgs e)
    {
        await StartMandatoryUpdateDownloadAsync();
    }

    private async void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(manual: true);
    }

    private async void UpdateNowBtn_Click(object sender, RoutedEventArgs e)
    {
        await BeginMandatoryUpdateAsync(_pendingUpdate);
    }

    // ============================================================
    // ТРЕЙ И ЗАВЕРШЕНИЕ РАБОТЫ
    // ============================================================
    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Robust",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть лаунчер", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (ConfirmExit())
            {
                _isClosingForReal = true;
                Close();
            }
        }));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RemoveTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        Hide();
        FooterStatusText.Text = "Свёрнуто в системный трей";
    }

    private bool ConfirmExit()
    {
        var activeWindows = _processLauncher.GetActiveSessions().Count;
        if (!_processLauncher.IsContractActive && activeWindows == 0)
            return true;

        var answer = MessageBox.Show(
            this,
            "Сейчас выполняется задание.\n\n" +
            "Если закрыть лаунчер, окна Roblox будут закрыты, а неначисленный прогресс может потеряться.\n\n" +
            "Всё равно выйти?",
            "Подтвердите выход",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return answer == MessageBoxResult.Yes;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingForReal)
        {
            CleanupResources();
            return;
        }

        if (_localConfig.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!ConfirmExit())
        {
            e.Cancel = true;
            return;
        }

        _isClosingForReal = true;
        CleanupResources();
    }

    private void CleanupResources()
    {
        CancelPendingLaunchLoop();
        _uiRefreshTimer.Stop();
        _updateCheckTimer?.Stop();
        RemoveTrayIcon();
        _clientOptimizer.ForceRestoreTemporaryFps();
        _clientOptimizer.RestoreRobloxFpsGlobal();
        _wsClient.Dispose();
        _logWatcherService.Dispose();
        _antiAfkService.Dispose();
        _processLauncher.Dispose();
        _clientOptimizer.Dispose();
        _mutexManager.Dispose();
        _configService.Dispose();
        _updateService.Dispose();
    }
}

public sealed class AccountRowViewModel : INotifyPropertyChanged
{
    private string _username = string.Empty;
    private string _proxyDisplay = string.Empty;
    private string _statusText = string.Empty;
    private string _currentJobId = string.Empty;

    public long UserId { get; set; }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string ProxyDisplay
    {
        get => _proxyDisplay;
        set => SetField(ref _proxyDisplay, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string CurrentJobId
    {
        get => _currentJobId;
        set => SetField(ref _currentJobId, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
