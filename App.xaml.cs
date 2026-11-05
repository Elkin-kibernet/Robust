using System.Windows;
using RobloxWorker.Launcher.Services;

namespace RobloxWorker.Launcher;

public partial class App : Application
{
    public App()
    {
        // Инициализация хуков автообновления Velopack при самом старте процесса
        UpdateManagerService.InitializeVelopackHooks();
    }
}