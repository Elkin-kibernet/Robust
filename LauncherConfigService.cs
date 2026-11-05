using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Локальный конфиг лаунчера (%appdata%\RobloxWorker\config.json):
/// токен, адрес сервера, тема, трей и звук.
/// </summary>
public sealed class LauncherLocalConfig
{
    public string ApiToken { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = "http://localhost:3000";
    public string Theme { get; set; } = "dark";
    public bool MinimizeToTray { get; set; }
    public bool SoundOnComplete { get; set; } = true;
    public bool AutoFlowEnabled { get; set; }
    public string AutoFlowFilter { get; set; } = "price";

    /// <summary>
    /// Unix-время (мс) окончания серверного штрафа за досрочный выход.
    /// Дублируется локально, чтобы отсчёт не сбрасывался перезапуском.
    /// </summary>
    public long PenaltyUntil { get; set; }
}

public sealed class LauncherConfigService
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string ConfigDirectory { get; }
    public string ConfigFilePath { get; }

    public LauncherConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobloxWorker");
        Directory.CreateDirectory(ConfigDirectory);
        ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
    }

    public LauncherLocalConfig Load()
    {
        LauncherLocalConfig config = new();
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                config = JsonSerializer.Deserialize<LauncherLocalConfig>(json, _jsonOptions) ?? new LauncherLocalConfig();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Config] Не удалось прочитать config.json: {ex.Message}");
        }

        config.ServerUrl = NormalizeServerUrl(config.ServerUrl);
        config.Theme = string.Equals(config.Theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        if (config.PenaltyUntil < 0) config.PenaltyUntil = 0;
        if (!new[] { "price", "short", "any" }.Contains(config.AutoFlowFilter))
            config.AutoFlowFilter = "price";

        var imported = false;

        // Настройки, встроенные сервером в хвост самого файла Robust.
        var embedded = TryReadEmbeddedConfig();
        if (string.IsNullOrWhiteSpace(config.ApiToken) && !string.IsNullOrWhiteSpace(embedded.Token))
        {
            config.ApiToken = embedded.Token;
            imported = true;
        }
        if (!string.IsNullOrWhiteSpace(embedded.ServerUrl))
        {
            var normalizedServer = NormalizeServerUrl(embedded.ServerUrl);
            if (!string.Equals(config.ServerUrl, normalizedServer, StringComparison.OrdinalIgnoreCase))
            {
                config.ServerUrl = normalizedServer;
                imported = true;
            }
        }

        // Совместимость со старыми сборками: token.txt / config.json рядом с файлом.
        var bundled = TryReadBundledConfig();
        if (string.IsNullOrWhiteSpace(config.ApiToken) && !string.IsNullOrWhiteSpace(bundled.Token))
        {
            config.ApiToken = bundled.Token;
            imported = true;
        }

        if (imported)
        {
            try { Save(config); } catch { /* не критично */ }
        }

        return config;
    }

    public void Save(LauncherLocalConfig config)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                var json = JsonSerializer.Serialize(config, _jsonOptions);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Config] Не удалось сохранить config.json: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// token.txt или config.json рядом с .exe — вложены в ZIP при скачивании с сайта.
    /// Из config.json дополнительно подхватывается базовый URL сайта.
    /// </summary>
    /// <summary>
    /// Сервер дописывает конфиг в хвост PE-файла. Читаем только последние 64 КБ,
    /// чтобы не загружать весь 70-мегабайтный файл.
    /// </summary>
    private (string Token, string ServerUrl) TryReadEmbeddedConfig()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
                return (string.Empty, string.Empty);

            const int maxTailBytes = 64 * 1024;
            using var stream = new FileStream(processPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(maxTailBytes, stream.Length);
            if (length <= 0) return (string.Empty, string.Empty);

            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            if (read <= 0) return (string.Empty, string.Empty);

            var marker = Encoding.ASCII.GetBytes("ROBUSTCFG:");
            var markerIndex = buffer.AsSpan(0, read).LastIndexOf(marker);
            if (markerIndex < 0) return (string.Empty, string.Empty);

            var start = markerIndex + marker.Length;
            var end = Array.IndexOf(buffer, (byte)'\n', start);
            if (end < 0) end = read;
            if (end <= start) return (string.Empty, string.Empty);

            var base64 = Encoding.ASCII.GetString(buffer, start, end - start).Trim();
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var token = root.TryGetProperty("apiToken", out var tokenElement)
                ? (tokenElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var serverUrl = root.TryGetProperty("serverUrl", out var serverElement)
                ? (serverElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            return (IsTokenLike(token) ? token : string.Empty, serverUrl);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Config] Не удалось прочитать встроенный конфиг: {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    private (string Token, string ServerUrl) TryReadBundledConfig()
    {
        var token = string.Empty;
        var serverUrl = string.Empty;

        foreach (var directory in GetSearchDirectories())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    var tokenFile = Path.Combine(directory, "token.txt");
                    if (File.Exists(tokenFile))
                    {
                        var value = File.ReadAllText(tokenFile).Trim();
                        if (IsTokenLike(value)) token = value;
                    }
                }

                var configFile = Path.Combine(directory, "config.json");
                if (File.Exists(configFile))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(configFile));
                    var root = document.RootElement;

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        foreach (var key in new[] { "apiToken", "api_token", "token", "workerApiToken", "worker_api_token" })
                        {
                            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                var candidate = (value.GetString() ?? string.Empty).Trim();
                                if (IsTokenLike(candidate))
                                {
                                    token = candidate;
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(serverUrl))
                    {
                        foreach (var key in new[] { "serverUrl", "server_url", "baseUrl", "base_url" })
                        {
                            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                var candidate = (value.GetString() ?? string.Empty).Trim();
                                if (!string.IsNullOrWhiteSpace(candidate))
                                {
                                    serverUrl = candidate;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Config] Ошибка чтения вложенного конфига: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(serverUrl))
                break;
        }

        return (token, serverUrl);
    }

    private static IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (seen.Add(candidate)) yield return candidate;
        }
    }

    private static bool IsTokenLike(string token)
        => !string.IsNullOrWhiteSpace(token) && token.Length >= 12 && !token.Contains(' ');

    public static string NormalizeServerUrl(string value)
    {
        var url = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            return "http://localhost:3000";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url.TrimEnd('/');
    }

    public static string BuildWorkerWsUrl(string serverUrl)
    {
        var normalized = NormalizeServerUrl(serverUrl);
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + normalized.Substring("https://".Length) + "/ws/worker";
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + normalized.Substring("http://".Length) + "/ws/worker";
        return normalized + "/ws/worker";
    }
}
