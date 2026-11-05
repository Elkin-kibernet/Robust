using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RobloxWorker.Launcher.Models;

public sealed class WsEnvelope
{
    [JsonPropertyName("event")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("timestamp_utc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}

public sealed class HeartbeatPayload
{
    [JsonPropertyName("client_version")]
    public string ClientVersion { get; set; } = "1.0.0";

    [JsonPropertyName("total_accounts_loaded")]
    public int TotalAccountsLoaded { get; set; }

    [JsonPropertyName("is_contract_active")]
    public bool IsContractActive { get; set; }

    [JsonPropertyName("is_in_timeout")]
    public bool IsInTimeout { get; set; }

    [JsonPropertyName("remaining_timeout_seconds")]
    public int RemainingTimeoutSeconds { get; set; }

    [JsonPropertyName("daily_early_exits")]
    public int DailyEarlyExits { get; set; }

    [JsonPropertyName("active_order_id")]
    public string? ActiveOrderId { get; set; }

    [JsonPropertyName("active_order_mode")]
    public string? ActiveOrderMode { get; set; }

    [JsonPropertyName("locked_slots_count")]
    public int LockedSlotsCount { get; set; }

    [JsonPropertyName("active_sessions")]
    public List<HeartbeatSessionItem> ActiveSessions { get; set; } = new();
}

public sealed class HeartbeatSessionItem
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("place_id")]
    public long PlaceId { get; set; }

    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }

    [JsonPropertyName("server_ip")]
    public string? ServerIp { get; set; }

    [JsonPropertyName("ticket_hash")]
    public string? TicketHash { get; set; }

    [JsonPropertyName("is_verified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("presence_verified")]
    public bool PresenceVerified { get; set; }

    [JsonPropertyName("process_id")]
    public int ProcessId { get; set; }

    [JsonPropertyName("uptime_seconds")]
    public int UptimeSeconds { get; set; }
}

public sealed class ContractViolationPayload
{
    [JsonPropertyName("order_id")]
    public string? OrderId { get; set; }

    [JsonPropertyName("order_mode")]
    public string? OrderMode { get; set; }

    [JsonPropertyName("violated_user_id")]
    public long? ViolatedUserId { get; set; }

    [JsonPropertyName("violated_process_id")]
    public int? ViolatedProcessId { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("daily_early_exits")]
    public int DailyEarlyExits { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Ответ сервера на heartbeat: профиль и баланс для шапки лаунчера.
/// </summary>
public sealed class AccountStateMessage
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("is_admin")]
    public bool IsAdmin { get; set; }

    [JsonPropertyName("main_balance_rub")]
    public decimal MainBalanceRub { get; set; }

    [JsonPropertyName("pending_earnings_rub")]
    public decimal PendingEarningsRub { get; set; }

    [JsonPropertyName("active_order_id")]
    public string? ActiveOrderId { get; set; }

    [JsonPropertyName("target_seconds")]
    public int TargetSeconds { get; set; }

    [JsonPropertyName("penalty_until")]
    public long PenaltyUntil { get; set; }

    [JsonPropertyName("penalty_remaining_seconds")]
    public int PenaltyRemainingSeconds { get; set; }

    [JsonPropertyName("daily_early_exits")]
    public int DailyEarlyExits { get; set; }

    [JsonPropertyName("update_required")]
    public bool UpdateRequired { get; set; }

    [JsonPropertyName("latest_launcher_version")]
    public string LatestLauncherVersion { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed class ServerCommandMessage
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("order_id")]
    public string? OrderId { get; set; }

    [JsonPropertyName("order_mode")]
    public string OrderMode { get; set; } = "FLEX_POOL";

    [JsonPropertyName("place_id")]
    public long PlaceId { get; set; }

    [JsonPropertyName("requested_accounts_count")]
    public int RequestedAccountsCount { get; set; } = 1;

    [JsonPropertyName("update_url")]
    public string? UpdateUrl { get; set; }
}
