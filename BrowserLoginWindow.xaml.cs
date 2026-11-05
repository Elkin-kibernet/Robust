using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace RobloxWorker.Launcher;

public partial class BrowserLoginWindow : Window
{
    private readonly string _isolatedUserDataFolder;
    private readonly DispatcherTimer _cookiePollTimer;
    private bool _isCookieCaptured;

    /// <summary>
    /// Перехваченная живая кука .ROBLOSECURITY после успешного входа.
    /// </summary>
    public string? CapturedRobloSecurity { get; private set; }

    public BrowserLoginWindow()
    {
        InitializeComponent();

        // Каждый запуск этого окна использует совершенно чистую временную папку (как новое окно Инкогнито)
        _isolatedUserDataFolder = Path.Combine(
            Path.GetTempPath(),
            "RobloxWorkerLauncher_WebView",
            $"Session_{Guid.NewGuid():N}");

        _cookiePollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _cookiePollTimer.Tick += CookiePollTimer_Tick;

        Loaded += BrowserLoginWindow_Loaded;
        Closing += BrowserLoginWindow_Closing;
    }

    private async void BrowserLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_isolatedUserDataFolder);

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection"
            };

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _isolatedUserDataFolder,
                options: options);

            await LoginWebView.EnsureCoreWebView2Async(env);

            // Очищаем любые старые куки на всякий случай и открываем страницу входа/регистрации
            LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
            LoginWebView.CoreWebView2.Navigate("https://www.roblox.com/login");

            _cookiePollTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить встроенный браузер WebView2: {ex.Message}",
                "Ошибка браузера",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async void CookiePollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isCookieCaptured || LoginWebView.CoreWebView2 is null)
            return;

        try
        {
            var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var robloSecurity = cookies.FirstOrDefault(c =>
                string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.Value) &&
                c.Value.Length > 100);

            if (robloSecurity is not null)
            {
                _isCookieCaptured = true;
                _cookiePollTimer.Stop();

                CapturedRobloSecurity = robloSecurity.Value;
                DialogResult = true;
                Close();
            }
        }
        catch
        {
            // Игнорируем промежуточные состояния во время навигации
        }
    }

    private void BrowserLoginWindow_Closing(object? sender, CancelEventArgs e)
    {
        _cookiePollTimer.Stop();

        try
        {
            LoginWebView.Dispose();
        }
        catch
        {
            // Игнорируем
        }

        // Удаляем временную папку профиля в фоне (не вызывая Log Out на сервере Roblox!)
        var folderToDelete = _isolatedUserDataFolder;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            try
            {
                if (Directory.Exists(folderToDelete))
                {
                    Directory.Delete(folderToDelete, recursive: true);
                }
            }
            catch
            {
                // Если файл ещё занят процессом Edge, Windows сама очистит %TEMP% позже
            }
        });
    }
}