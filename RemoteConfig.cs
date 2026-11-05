using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RobloxWorker.Launcher.Models;

/// <summary>
/// Полная модель динамической конфигурации, получаемой с бэкенда (GET /api/v1/client/config).
/// Никакого хардкода в логике лаунчера — все сервисы читают параметры отсюда.
/// </summary>
public sealed class RemoteConfig
{
    [JsonPropertyName("config_version")]
    public int ConfigVersion { get; set; } = 3;

    [JsonPropertyName("min_supported_client_version")]
    public string MinSupportedClientVersion { get; set; } = "1.0.0";

    [JsonPropertyName("latest_client_version")]
    public string LatestClientVersion { get; set; } = "1.0.0";

    [JsonPropertyName("update_feed_url")]
    public string UpdateFeedUrl { get; set; } = "https://api.yourdomain.com/releases";

    [JsonPropertyName("mutex_name")]
    public string MutexName { get; set; } = "ROBLOX_singletonEvent";

    [JsonPropertyName("max_windows_per_ip")]
    public int MaxWindowsPerIp { get; set; } = 3;

    [JsonPropertyName("anti_afk_interval_seconds")]
    public int AntiAfkIntervalSeconds { get; set; } = 600; // 10 минут

    [JsonPropertyName("anti_afk_virtual_key_code")]
    public int AntiAfkVirtualKeyCode { get; set; } = 0x20; // VK_SPACE (Пробел) или 0x0D (Enter)

    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    [JsonPropertyName("reconnect_delay_seconds")]
    public int ReconnectDelaySeconds { get; set; } = 15;

    [JsonPropertyName("launch_args_template")]
    public string LaunchArgsTemplate { get; set; } =
        "roblox-player:1+launchmode:play+gameinfo:{AuthTicket}+launchtime:{LaunchTime}+placelauncherurl:{EncodedPlaceLauncherUrl}+browsertrackerid:{BrowserTrackerId}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

    /// <summary>
    /// Динамические флаги ClientAppSettings.json (из разрешённого Allowlist Roblox для минимальной графики).
    /// </summary>
    [JsonPropertyName("client_app_settings")]
    public Dictionary<string, object> ClientAppSettings { get; set; } = new();

    /// <summary>
    /// Регулярные выражения для парсинга логов Roblox (меняются с сервера при обновлениях Roblox).
    /// </summary>
    [JsonPropertyName("log_regex_patterns")]
    public LogRegexPatterns LogPatterns { get; set; } = new();

    /// <summary>
    /// Создаёт безопасный конфиг по умолчанию (используется до первого ответа сервера или в режиме разработки).
    /// </summary>
    public static RemoteConfig CreateDefault()
    {
        return new RemoteConfig
        {
            ConfigVersion = 3,
            MinSupportedClientVersion = "1.0.0",
            LatestClientVersion = "1.0.0",
            MutexName = "ROBLOX_singletonEvent",
            MaxWindowsPerIp = 3,
            AntiAfkIntervalSeconds = 600,
            AntiAfkVirtualKeyCode = 0x20,
            HeartbeatIntervalSeconds = 30,
            ReconnectDelaySeconds = 15,
            LaunchArgsTemplate =
                "roblox-player:1+launchmode:play+gameinfo:{AuthTicket}+launchtime:{LaunchTime}+placelauncherurl:{EncodedPlaceLauncherUrl}+browsertrackerid:{BrowserTrackerId}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp",
            ClientAppSettings = new Dictionary<string, object>
            {
                // Разрешённые флаги Allowlist для экстремального снижения нагрузки на GPU и CPU:
                ["DFIntDebugFRMQualityLevelOverride"] = 1,
                ["DFFlagDebugPauseVoxelizer"] = true,
                ["FFlagDebugSkyGray"] = true,
                ["DFFlagTextureQualityOverrideEnabled"] = true,
                ["DFIntTextureQualityOverride"] = 0,
                ["FIntDebugForceMSAASamples"] = 1,
                ["DFFlagDisableDPIScale"] = true,
                ["FIntFRMMaxGrassDistance"] = 0,
                ["FIntFRMMinGrassDistance"] = 0,
                ["DFIntCSGLevelOfDetailSwitchingDistance"] = 0,
                ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = 0,
                ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = 0,
                ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = 0,
                ["FIntGrassMovementReducedMotionFactor"] = 0,
                ["FFlagDebugGraphicsPreferD3D11"] = true
            },
            LogPatterns = new LogRegexPatterns
            {
                ServerJoinedPattern = @"Joining game '(?<JobId>[0-9a-fA-F\-]{36})' place (?<PlaceId>\d+)",
                ConnectionAcceptedPattern = @"Connection accepted from (?<ServerIp>[\d\.:]+)",
                DisconnectPattern = @"(Lost connection with reason|Disconnection Notification.*Reason:\s*)(?<ErrorCode>\d+)"
            }
        };
    }
}

public sealed class LogRegexPatterns
{
    [JsonPropertyName("server_joined_pattern")]
    public string ServerJoinedPattern { get; set; } = string.Empty;

    [JsonPropertyName("connection_accepted_pattern")]
    public string ConnectionAcceptedPattern { get; set; } = string.Empty;

    [JsonPropertyName("disconnect_pattern")]
    public string DisconnectPattern { get; set; } = string.Empty;
}