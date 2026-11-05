using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Сервис удержания активности (Anti-AFK) на базе аппаратного стека Windows SendInput (ScanCodes).
/// Выполняет настраиваемую пользователем последовательность действий (прыжок, ходьба W->S, стрейф A->D,
/// поворот камеры, зум, клик мыши) и строго соблюдает выбранный пользователем режим окна (WindowMode).
/// </summary>
public sealed class AntiAfkService : IDisposable
{
    private readonly ProcessLauncher _processLauncher;
    private readonly RemoteConfigService _configService;
    private readonly ClientOptimizer _clientOptimizer;
    private readonly PeriodicTimer _tickTimer;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _loopTask;
    private bool _isDisposed;

    // Аппаратные скан-коды клавиш (DirectInput / RawInput)
    private const ushort SC_SPACE = 0x39;
    private const ushort SC_W = 0x11;
    private const ushort SC_S = 0x1F;
    private const ushort SC_A = 0x1E;
    private const ushort SC_D = 0x20;
    private const ushort SC_I = 0x17;
    private const ushort SC_O = 0x18;
    private const ushort SC_LEFT_ARROW = 0x4B;
    private const ushort SC_RIGHT_ARROW = 0x4D;

    /// <summary>
    /// Срабатывает после выполнения цепочки Anti-AFK в конкретном окне.
    /// </summary>
    public event Action<ActiveGameSession, string>? AntiAfkPulseSent;

    public AntiAfkService(
        ProcessLauncher processLauncher,
        RemoteConfigService configService,
        ClientOptimizer clientOptimizer)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _clientOptimizer = clientOptimizer ?? throw new ArgumentNullException(nameof(clientOptimizer));

        _tickTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    }

    public void Start()
    {
        if (_loopTask is not null)
            return;

        _loopTask = Task.Run(() => RunAntiAfkLoopAsync(_cts.Token));
        Debug.WriteLine("[AntiAfkService] Сервис аппаратной цепочки Anti-AFK (SendInput) запущен.");
    }

    /// <summary>
    /// Принудительно выполняет настроенную пользователем цепочку действий Anti-AFK по всем активным окнам прямо сейчас.
    /// </summary>
    public async Task<int> TriggerImmediateAntiAfkForAllAsync(CancellationToken ct = default)
    {
        var sessions = _processLauncher.GetActiveSessions();
        if (sessions.Count == 0)
            return 0;

        await _executionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previousForeground = GetForegroundWindow();
            GetCursorPos(out var savedCursor);

            int index = 0;
            int successCount = 0;

            foreach (var session in sessions)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (await ExecuteSequenceOnSessionInternalAsync(session, index, ct).ConfigureAwait(false))
                {
                    successCount++;
                }

                index++;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            // Возвращаем курсор мыши и фокус на то окно, в котором работал пользователь
            SetCursorPos(savedCursor.X, savedCursor.Y);
            if (previousForeground != nint.Zero && IsWindow(previousForeground))
            {
                ForceForegroundWindow(previousForeground);
            }

            return successCount;
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private async Task RunAntiAfkLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _tickTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var optSettings = _clientOptimizer.CurrentSettings;
                var baseIntervalSeconds = Math.Max(45, optSettings.AntiAfkIntervalMinutes * 60);

                var sessions = _processLauncher.GetActiveSessions();
                if (sessions.Count == 0)
                    continue;

                var now = DateTime.UtcNow;
                int index = 0;

                foreach (var session in sessions)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var jitterSeconds = Random.Shared.Next(-baseIntervalSeconds / 12, (baseIntervalSeconds / 20) + 1);
                    var effectiveInterval = TimeSpan.FromSeconds(baseIntervalSeconds + jitterSeconds);

                    if (now - session.LastAntiAfkSentUtc >= effectiveInterval)
                    {
                        await _executionLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var previousForeground = GetForegroundWindow();
                            GetCursorPos(out var savedCursor);

                            await ExecuteSequenceOnSessionInternalAsync(session, index, ct).ConfigureAwait(false);

                            SetCursorPos(savedCursor.X, savedCursor.Y);
                            if (previousForeground != nint.Zero && IsWindow(previousForeground))
                            {
                                ForceForegroundWindow(previousForeground);
                            }
                        }
                        finally
                        {
                            _executionLock.Release();
                        }

                        await Task.Delay(400, ct).ConfigureAwait(false);
                    }

                    index++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка сервиса
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AntiAfkService] Ошибка в цикле Anti-AFK: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет выбранную пользователем цепочку движений в конкретном окне Roblox через аппаратный SendInput.
    /// </summary>
    private async Task<bool> ExecuteSequenceOnSessionInternalAsync(
        ActiveGameSession session,
        int windowIndex,
        CancellationToken ct)
    {
        var hwnd = ResolveWindowHandle(session);
        if (hwnd == nint.Zero)
            return false;

        var optSettings = _clientOptimizer.CurrentSettings;
        session.IsAntiAfkWakeupInProgress = true;

        try
        {
            // 1. Подготавливаем окно к приёму аппаратного ввода RawInput:
            // Если окно было свёрнуто в панель задач, разворачиваем его на время цепочки.
            // Если окно было за пределами экрана (OffScreen), временно возвращаем в видимую зону, чтобы клик и фокус сработали.
            if (IsIconic(hwnd))
            {
                if (!optSettings.SmartAntiAfkWakeup && optSettings.WindowMode == RobloxWindowMode.Minimized)
                {
                    // Если пользователь сам отключил пробуждение свёрнутых окон — пропускаем разворачивание
                }
                else
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    await Task.Delay(220, ct).ConfigureAwait(false);
                }
            }
            else if (optSettings.WindowMode == RobloxWindowMode.OffScreen)
            {
                SetWindowPos(hwnd, nint.Zero, 20, 20, 200, 150, SWP_NOZORDER);
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            // 2. Переводим фокус ввода Windows на окно Roblox, чтобы его RawInput-хук принял скан-коды клавиш
            ForceForegroundWindow(hwnd);
            await Task.Delay(200, ct).ConfigureAwait(false);

            // 3. Наводим курсор в центр окна Roblox (и делаем клик ЛКМ, если он включён в цепочке)
            if (GetWindowRect(hwnd, out var rect))
            {
                int centerX = rect.Left + Math.Max(40, (rect.Right - rect.Left) / 2);
                int centerY = rect.Top + Math.Max(40, (rect.Bottom - rect.Top) / 2);

                if (centerX > 0 && centerY > 0)
                {
                    SetCursorPos(centerX, centerY);
                    await Task.Delay(50, ct).ConfigureAwait(false);

                    // Микро-сдвиг мыши внутри окна (активирует viewport Roblox)
                    SendMouseMoveRelative(2, 2);
                    await Task.Delay(40, ct).ConfigureAwait(false);

                    if (optSettings.AfkActionMouseClick)
                    {
                        await SendMouseLeftClickAsync(ct).ConfigureAwait(false);
                        await Task.Delay(120, ct).ConfigureAwait(false);
                    }
                }
            }

            // 4. Выполняем выбранную пользователем последовательность клавиш через SendInput (ScanCodes):
            bool anyKeyActionEnabled =
                optSettings.AfkActionJump ||
                optSettings.AfkActionWalkForwardBack ||
                optSettings.AfkActionStrafeLeftRight ||
                optSettings.AfkActionCameraRotate ||
                optSettings.AfkActionCameraZoom;

            // Если пользователь случайно снял все галочки — делаем хотя бы базовый прыжок
            if (optSettings.AfkActionJump || !anyKeyActionEnabled)
            {
                await SendHardwareKeyHoldAsync(SC_SPACE, isExtendedKey: false, holdMs: 180, ct).ConfigureAwait(false);
                await Task.Delay(140, ct).ConfigureAwait(false);
            }

            if (optSettings.AfkActionWalkForwardBack)
            {
                // Идём вперёд (W) 350 мс и возвращаемся назад (S) 350 мс на исходную точку
                await SendHardwareKeyHoldAsync(SC_W, isExtendedKey: false, holdMs: 350, ct).ConfigureAwait(false);
                await Task.Delay(110, ct).ConfigureAwait(false);
                await SendHardwareKeyHoldAsync(SC_S, isExtendedKey: false, holdMs: 350, ct).ConfigureAwait(false);
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            if (optSettings.AfkActionStrafeLeftRight)
            {
                // Шаг влево (A) 300 мс и шаг вправо (D) 300 мс
                await SendHardwareKeyHoldAsync(SC_A, isExtendedKey: false, holdMs: 300, ct).ConfigureAwait(false);
                await Task.Delay(110, ct).ConfigureAwait(false);
                await SendHardwareKeyHoldAsync(SC_D, isExtendedKey: false, holdMs: 300, ct).ConfigureAwait(false);
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            if (optSettings.AfkActionCameraRotate)
            {
                // Поворот камеры стрелкой влево (Extended ScanCode) и возврат стрелкой вправо
                await SendHardwareKeyHoldAsync(SC_LEFT_ARROW, isExtendedKey: true, holdMs: 260, ct).ConfigureAwait(false);
                await Task.Delay(90, ct).ConfigureAwait(false);
                await SendHardwareKeyHoldAsync(SC_RIGHT_ARROW, isExtendedKey: true, holdMs: 260, ct).ConfigureAwait(false);
                await Task.Delay(110, ct).ConfigureAwait(false);
            }

            if (optSettings.AfkActionCameraZoom)
            {
                // Приближение (I) и отдаление (O) камеры
                await SendHardwareKeyHoldAsync(SC_I, isExtendedKey: false, holdMs: 160, ct).ConfigureAwait(false);
                await Task.Delay(90, ct).ConfigureAwait(false);
                await SendHardwareKeyHoldAsync(SC_O, isExtendedKey: false, holdMs: 160, ct).ConfigureAwait(false);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            // Даём клиенту 180 мс на отправку пакетов физики на сервер Roblox
            await Task.Delay(180, ct).ConfigureAwait(false);

            // 5. Возвращаем окно строго в тот режим, который выбрал пользователь в настройках!
            // Если выбрано «Обычное окно» или «Мини-плитка» — окно НЕ сворачивается!
            ClientOptimizer.ApplyWindowModeToHandle(hwnd, windowIndex, optSettings.WindowMode);

            if (optSettings.RamMode != RamCompressionLevel.Disabled)
            {
                _clientOptimizer.TrimSingleProcessMemory(session.ProcessId);
            }

            session.LastAntiAfkSentUtc = DateTime.UtcNow;
            AntiAfkPulseSent?.Invoke(session, optSettings.GetActiveActionsSummary());
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AntiAfkService] Ошибка выполнения цепочки для PID {session.ProcessId}: {ex.Message}");
            return false;
        }
        finally
        {
            session.IsAntiAfkWakeupInProgress = false;
        }
    }

    /// <summary>
    /// Отправляет аппаратное нажатие и отпускание клавиши по скан-коду через системный SendInput.
    /// </summary>
    private static async Task SendHardwareKeyHoldAsync(ushort scanCode, bool isExtendedKey, int holdMs, CancellationToken ct)
    {
        uint downFlags = KEYEVENTF_SCANCODE | (isExtendedKey ? KEYEVENTF_EXTENDEDKEY : 0u);
        uint upFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (isExtendedKey ? KEYEVENTF_EXTENDEDKEY : 0u);

        var downInput = new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = downFlags,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, new[] { downInput }, Marshal.SizeOf<INPUT>());

        await Task.Delay(holdMs, ct).ConfigureAwait(false);

        var upInput = new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = upFlags,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, new[] { upInput }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseMoveRelative(int dx, int dy)
    {
        var moveInput = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = MOUSEEVENTF_MOVE
                }
            }
        };

        SendInput(1, new[] { moveInput }, Marshal.SizeOf<INPUT>());
    }

    private static async Task SendMouseLeftClickAsync(CancellationToken ct)
    {
        var down = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN }
            }
        };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());

        await Task.Delay(75, ct).ConfigureAwait(false);

        var up = new INPUT
        {
            Type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP }
            }
        };
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Надёжно переводит фокус ввода Windows на целевое окно с обходом блокировки ForegroundLockTimeout.
    /// </summary>
    private static void ForceForegroundWindow(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero || !IsWindow(targetHwnd))
            return;

        nint foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd == targetHwnd)
            return;

        uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);
        uint currentThreadId = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
        {
            attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
        }

        BringWindowToTop(targetHwnd);
        SetForegroundWindow(targetHwnd);

        if (attached)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    private static nint ResolveWindowHandle(ActiveGameSession session)
    {
        if (session.MainWindowHandle != nint.Zero && IsWindow(session.MainWindowHandle))
        {
            return session.MainWindowHandle;
        }

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            process.Refresh();

            if (!process.HasExited && process.MainWindowHandle != nint.Zero)
            {
                session.MainWindowHandle = process.MainWindowHandle;
                return session.MainWindowHandle;
            }
        }
        catch
        {
            // Процесс ещё создаёт окно или уже завершился
        }

        return nint.Zero;
    }

    #region Win32 SendInput & Window Focus API

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const int SW_RESTORE = 9;
    private const uint SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

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
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts.Cancel();
        _tickTimer.Dispose();
        _executionLock.Dispose();
        _cts.Dispose();
    }
}