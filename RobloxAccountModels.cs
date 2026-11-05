using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RobloxWorker.Launcher.Models;

/// <summary>
/// Данные аккаунта воркера, хранящиеся локально на ПК (кука хранится в зашифрованном через DPAPI виде).
/// </summary>
public sealed class WorkerAccount
{
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Зашифрованная через Windows DPAPI строка .ROBLOSECURITY (Base64).
    /// </summary>
    public string EncryptedRobloSecurity { get; set; } = string.Empty;

    /// <summary>
    /// Открыты ли настройки приватности (whoCanJoinMeInExperiences = AllUsers) для серверного Анти-Фрода.
    /// </summary>
    public bool IsPrivacyConfigured { get; set; }

    /// <summary>
    /// Опциональный прокси в формате socks5://ip:port или http://ip:port
    /// </summary>
    public string? ProxyAddress { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }

    /// <summary>
    /// Пользовательская заметка к аккаунту (отображается в окне настройки).
    /// </summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Ответ официального API Roblox (https://users.roblox.com/v1/users/authenticated).
/// </summary>
public sealed class RobloxAuthenticatedUserResponse
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Запрос к официальному API присутствия Roblox (POST https://presence.roblox.com/v1/presence/users).
/// </summary>
public sealed class RobloxPresenceRequest
{
    [JsonPropertyName("userIds")]
    public List<long> UserIds { get; set; } = new();
}

/// <summary>
/// Ответ официального API присутствия Roblox для Анти-Фрод верификации.
/// </summary>
public sealed class RobloxPresenceResponse
{
    [JsonPropertyName("userPresences")]
    public List<RobloxUserPresenceItem> UserPresences { get; set; } = new();
}

public sealed class RobloxUserPresenceItem
{
    /// <summary>
    /// 0 = Offline, 1 = Online (Website), 2 = InGame, 3 = InStudio
    /// </summary>
    [JsonPropertyName("userPresenceType")]
    public int UserPresenceType { get; set; }

    [JsonPropertyName("lastLocation")]
    public string? LastLocation { get; set; }

    [JsonPropertyName("placeId")]
    public long? PlaceId { get; set; }

    [JsonPropertyName("rootPlaceId")]
    public long? RootPlaceId { get; set; }

    /// <summary>
    /// Официальный JobId сервера, в котором реально находится игрок по данным Roblox.
    /// </summary>
    [JsonPropertyName("gameId")]
    public string? GameId { get; set; }

    [JsonPropertyName("universeId")]
    public long? UniverseId { get; set; }

    [JsonPropertyName("userId")]
    public long UserId { get; set; }
}

/// <summary>
/// Результат успешного получения одноразового билета для запуска игры.
/// </summary>
public sealed class AuthTicketResult
{
    public bool IsSuccess { get; init; }
    public string? AuthTicket { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime ObtainedAtUtc { get; init; } = DateTime.UtcNow;

    public static AuthTicketResult Success(string ticket) => new()
    {
        IsSuccess = true,
        AuthTicket = ticket
    };

    public static AuthTicketResult Failure(string error) => new()
    {
        IsSuccess = false,
        ErrorMessage = error
    };
}