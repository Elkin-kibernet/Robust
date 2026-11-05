using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Сервис синхронизации динамической конфигурации с сервером (Remote Config).
/// Обновляет параметры каждые 10 минут без перезапуска лаунчера.
/// </summary>
public sealed class RemoteConfigService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly UpdateManagerService _updateManager;
    private string _configEndpointUrl;
    private readonly string _localCacheFilePath;
    private readonly PeriodicTimer _refreshTimer;
    private readonly CancellationTokenSource _cts = new();

    private RemoteConfig _currentConfig;
    private Task? _backgroundLoopTask;
    private bool _isDisposed;

    /// <summary>
    /// Текущий актуальный конфиг (потокобезопасное чтение).
    /// </summary>
    public RemoteConfig CurrentConfig => Volatile.Read(ref _currentConfig);

    /// <summary>
    /// Срабатывает каждый раз, когда с сервера пришёл обновлённый конфиг.
    /// </summary>
    public event Action<RemoteConfig>? ConfigUpdated;

    /// <summary>
    /// Срабатывает, если сервер заблокировал работу из-за устаревшей версии клиента (Force Update).
    /// </summary>
    public event Func<string, Task>? ForceUpdateRequired;

    public RemoteConfigService(
        UpdateManagerService updateManager,
        string apiBaseUrl = "https://api.yourdomain.com")
    {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _configEndpointUrl = $"{apiBaseUrl.TrimEnd('/')}/api/v1/client/config";

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxWorkerLauncher");
        Directory.CreateDirectory(appDataDir);
        _localCacheFilePath = Path.Combine(appDataDir, "remote_config_cache.json");

        // Загружаем кэш с диска или безопасный дефолтный конфиг
        _currentConfig = LoadCachedOrDefaultConfig();

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("X-Client-Version", _updateManager.CurrentVersion);

        // Таймер обновления каждые 10 минут (по ТЗ)
        _refreshTimer = new PeriodicTimer(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Устанавливает API-токен воркера для авторизованных запросов к серверу.
    /// </summary>
    public void SetWorkerApiToken(string apiToken)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
            return;

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    /// <summary>
    /// Меняет адрес бэкенда без перезапуска лаунчера (после смены домена).
    /// </summary>
    public void SetApiBaseUrl(string apiBaseUrl)
    {
        var normalized = LauncherConfigService.NormalizeServerUrl(apiBaseUrl);
        _configEndpointUrl = $"{normalized}/api/v1/client/config";
    }

    /// <summary>
    /// Выполняет первичную загрузку конфига при старте и запускает фоновый цикл обновления каждые 10 минут.
    /// </summary>
    public async Task InitializeAndStartLoopAsync()
    {
        await FetchConfigFromServerAsync(_cts.Token).ConfigureAwait(false);
        _backgroundLoopTask = Task.Run(() => RunPeriodicRefreshLoopAsync(_cts.Token));
    }

    private async Task RunPeriodicRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _refreshTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FetchConfigFromServerAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка при закрытии программы
        }
    }

    /// <summary>
    /// Запрашивает свежий конфиг с бэкенда, проверяет версию клиента и валидирует регулярки.
    /// </summary>
    public async Task<bool> FetchConfigFromServerAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_configEndpointUrl, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.UpgradeRequired)
            {
                Debug.WriteLine("[RemoteConfig] Сервер отклонил версию клиента (HTTP 426 Upgrade Required).");
                if (ForceUpdateRequired is not null)
                {
                    await ForceUpdateRequired.Invoke(_currentConfig.UpdateFeedUrl).ConfigureAwait(false);
                }
                return false;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var newConfig = JsonSerializer.Deserialize<RemoteConfig>(json);

            if (newConfig is null || !ValidateConfigSafety(newConfig))
            {
                Debug.WriteLine("[RemoteConfig] Получен некорректный или небезопасный конфиг с сервера. Игнорируем.");
                return false;
            }

            if (_updateManager.IsVersionOutdated(newConfig.MinSupportedClientVersion))
            {
                Debug.WriteLine($"[RemoteConfig] Текущая версия ({_updateManager.CurrentVersion}) ниже минимальной ({newConfig.MinSupportedClientVersion}).");
                if (ForceUpdateRequired is not null)
                {
                    await ForceUpdateRequired.Invoke(newConfig.UpdateFeedUrl).ConfigureAwait(false);
                }
                return false;
            }

            Volatile.Write(ref _currentConfig, newConfig);
            await SaveConfigToCacheAsync(json, ct).ConfigureAwait(false);
            ConfigUpdated?.Invoke(newConfig);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemoteConfig] Не удалось получить конфиг с сервера (используется локальный): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Защита от кривых настроек, устаревшего шаблона или ReDoS-регулярок.
    /// </summary>
    private static bool ValidateConfigSafety(RemoteConfig config)
    {
        if (config.ConfigVersion < 3)
            return false;

        if (string.IsNullOrWhiteSpace(config.MutexName))
            return false;

        if (string.IsNullOrWhiteSpace(config.LaunchArgsTemplate) ||
            config.LaunchArgsTemplate.TrimStart().StartsWith("--play", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (config.MaxWindowsPerIp is < 1 or > 50)
            return false;

        if (config.AntiAfkIntervalSeconds < 10)
            return false;

        try
        {
            var timeout = TimeSpan.FromMilliseconds(200);
            if (!string.IsNullOrWhiteSpace(config.LogPatterns.ServerJoinedPattern))
                _ = new Regex(config.LogPatterns.ServerJoinedPattern, RegexOptions.Compiled, timeout);

            if (!string.IsNullOrWhiteSpace(config.LogPatterns.ConnectionAcceptedPattern))
                _ = new Regex(config.LogPatterns.ConnectionAcceptedPattern, RegexOptions.Compiled, timeout);

            if (!string.IsNullOrWhiteSpace(config.LogPatterns.DisconnectPattern))
                _ = new Regex(config.LogPatterns.DisconnectPattern, RegexOptions.Compiled, timeout);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private RemoteConfig LoadCachedOrDefaultConfig()
    {
        try
        {
            if (File.Exists(_localCacheFilePath))
            {
                var json = File.ReadAllText(_localCacheFilePath);
                var cached = JsonSerializer.Deserialize<RemoteConfig>(json);
                if (cached is not null && ValidateConfigSafety(cached))
                {
                    return cached;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemoteConfig] Ошибка чтения кэша конфига: {ex.Message}");
        }

        return RemoteConfig.CreateDefault();
    }

    private async Task SaveConfigToCacheAsync(string json, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(_localCacheFilePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RemoteConfig] Ошибка сохранения кэша конфига: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts.Cancel();
        _refreshTimer.Dispose();
        _httpClient.Dispose();
        _cts.Dispose();
    }
}