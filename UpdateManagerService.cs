using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Сервис управления автообновлением клиента через Velopack.
/// Поддерживает как фоновую проверку при старте, так и Force Update по команде с бэкенда.
/// </summary>
public sealed class UpdateManagerService
{
    public string CurrentVersion { get; }

    public UpdateManagerService()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        CurrentVersion = version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";
    }

    /// <summary>
    /// Обязательный вызов в самом начале работы приложения (App.xaml.cs) для обработки хуков установки/обновления Velopack.
    /// </summary>
    public static void InitializeVelopackHooks()
    {
        VelopackApp.Build().Run();
    }

    /// <summary>
    /// Проверяет наличие обновлений на сервере релизов или GitHub Releases.
    /// Если найдена новая версия — скачивает её и перезапускает лаунчер.
    /// </summary>
    /// <param name="updateFeedUrl">URL папки с релизами Velopack или ссылка на репозиторий GitHub.</param>
    /// <param name="onProgress">Колбэк прогресса скачивания (от 0 до 100).</param>
    /// <returns>True, если обновление установлено и выполняется перезапуск; иначе False.</returns>
    public async Task<bool> CheckAndApplyUpdatesAsync(string updateFeedUrl, Action<int>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(updateFeedUrl))
            return false;

        try
        {
            IUpdateSource source = updateFeedUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(updateFeedUrl, accessToken: null, prerelease: false)
                : new SimpleWebSource(updateFeedUrl);

            var manager = new UpdateManager(source);

            // Если приложение запущено из редактора кода (Debug без упаковки в установщик), пропускаем автообновление
            if (!manager.IsInstalled)
            {
                Debug.WriteLine("[UpdateManager] Приложение запущено в режиме разработки (не установлено через Velopack). Пропуск проверки.");
                return false;
            }

            var newVersion = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (newVersion is null)
                return false; // У пользователя уже самая свежая версия

            // Скачиваем дельта-обновление
            await manager.DownloadUpdatesAsync(newVersion, progress =>
            {
                onProgress?.Invoke(progress);
            }).ConfigureAwait(false);

            // Применяем обновление и мгновенно перезапускаем лаунчер
            manager.ApplyUpdatesAndRestart(newVersion);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateManager] Ошибка при проверке обновлений: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Сравнивает текущую версию клиента с минимально допустимой версией с сервера.
    /// </summary>
    public bool IsVersionOutdated(string minSupportedVersion)
    {
        if (Version.TryParse(CurrentVersion, out var current) &&
            Version.TryParse(minSupportedVersion, out var minimum))
        {
            return current < minimum;
        }

        return false;
    }
}