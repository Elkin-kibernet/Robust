using System;

namespace RobloxWorker.Launcher.Models;

/// <summary>
/// Состояние одного запущенного окна RobloxPlayerBeta.exe с данными Анти-Фрод верификации и контроля контракта.
/// </summary>
public sealed class ActiveGameSession
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public required WorkerAccount Account { get; init; }
    public required long PlaceId { get; init; }
    public required int ProcessId { get; set; }
    public required string ProxyGroupKey { get; init; }
    public required string IsolatedTempDirectory { get; init; }

    /// <summary>
    /// Криптографический SHA-256 отпечаток одноразового билета AuthTicket (доказательство реального запуска).
    /// </summary>
    public string AuthTicketSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Системный дескриптор окна Windows (HWND).
    /// </summary>
    public nint MainWindowHandle { get; set; }

    /// <summary>
    /// ID конкретного сервера Roblox (из логов или официального Roblox Presence API).
    /// </summary>
    public string? CurrentJobId { get; set; }

    /// <summary>
    /// Внутренний IP:Port игрового сервера Roblox из строки лога 'Connection accepted from ...'
    /// (недоступен снаружи без реального входа клиента в 3D-мир).
    /// </summary>
    public string? ConnectedServerIp { get; set; }

    /// <summary>
    /// Подтверждено ли нахождение аккаунта в целевом PlaceId через официальный Roblox Presence API.
    /// </summary>
    public bool IsPresenceApiVerified { get; set; }

    /// <summary>
    /// Считается ли сессия полностью подтверждённой Анти-Фродом (для начисления оплаты).
    /// </summary>
    public bool IsFullyVerified =>
        IsPresenceApiVerified ||
        (!string.IsNullOrWhiteSpace(CurrentJobId) && !string.IsNullOrWhiteSpace(ConnectedServerIp));

    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastAntiAfkSentUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastRamTrimUtc { get; set; } = DateTime.MinValue;
    public DateTime LastPresenceCheckUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Флаг: прямо сейчас выполняется микро-пробуждение окна для цепочки Anti-AFK.
    /// </summary>
    public volatile bool IsAntiAfkWakeupInProgress;

    /// <summary>
    /// Зафиксирована ли в логах сетевая ошибка дисконнекта (например, 277 Lost connection).
    /// Если false и процесс завершился с кодом 0 — значит, окно закрыл вручную сам воркер.
    /// </summary>
    public volatile bool HasNetworkDisconnectInLogs;

    /// <summary>
    /// Последний код ошибки дисконнекта из логов Roblox.
    /// </summary>
    public int? LastDisconnectErrorCode { get; set; }

    /// <summary>
    /// Остановлено ли окно самим лаунчером (при плановой остановке, Watchdog или системном реконнекте).
    /// </summary>
    public volatile bool IsIntentionalStop;
}