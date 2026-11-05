using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Управляет системным мьютексом Windows для запуска нескольких окон Roblox одновременно.
/// Блокирует общие файлы сессии (RobloxCookies.dat / appStorage.json) ТОЛЬКО на время работы окон
/// и мгновенно освобождает их, когда окна закрыты.
/// </summary>
public sealed class MutexManager : IDisposable
{
    private readonly RemoteConfigService _configService;
    private readonly object _syncLock = new();

    private Thread? _mutexOwnerThread;
    private ManualResetEventSlim? _releaseSignal;
    private ManualResetEventSlim? _acquiredSignal;
    private FileStream? _cookiesLockStream;
    private FileStream? _appStorageLockStream;
    private string _activeMutexName = string.Empty;
    private volatile bool _isHoldingMutex;
    private bool _isDisposed;

    public bool IsMultiInstanceActive => _isHoldingMutex;

    public MutexManager(RemoteConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _configService.ConfigUpdated += OnRemoteConfigUpdated;
    }

    /// <summary>
    /// Запускает удержание системного мьютекса мультиоконности.
    /// </summary>
    public bool AcquireSingletonMutex()
    {
        var targetMutexName = _configService.CurrentConfig.MutexName;
        if (string.IsNullOrWhiteSpace(targetMutexName))
            targetMutexName = "ROBLOX_singletonEvent";

        ManualResetEventSlim acquiredWaitHandle;

        lock (_syncLock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(MutexManager));

            if (_isHoldingMutex && string.Equals(_activeMutexName, targetMutexName, StringComparison.Ordinal))
            {
                return true;
            }

            ReleaseCurrentMutexInternal();
            KillExistingRobloxProcessesIfNoneManaged();

            _releaseSignal = new ManualResetEventSlim(false);
            _acquiredSignal = new ManualResetEventSlim(false);
            acquiredWaitHandle = _acquiredSignal;
            _activeMutexName = targetMutexName;

            var releaseHandle = _releaseSignal;
            _mutexOwnerThread = new Thread(() => MutexThreadWorker(targetMutexName, releaseHandle, acquiredWaitHandle))
            {
                Name = $"RobloxMutexHolder_{targetMutexName}",
                IsBackground = true
            };

            _mutexOwnerThread.Start();
        }

        acquiredWaitHandle.Wait(TimeSpan.FromSeconds(5));
        return _isHoldingMutex;
    }

    /// <summary>
    /// Включает защиту файлов кук Roblox перед стартом окон ботов, чтобы окна не перезаписывали аккаунты друг друга.
    /// </summary>
    public void LockSharedLoginFiles()
    {
        lock (_syncLock)
        {
            try
            {
                var localStorageDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox",
                    "LocalStorage");

                Directory.CreateDirectory(localStorageDir);

                var cookiesFilePath = Path.Combine(localStorageDir, "RobloxCookies.dat");
                var appStorageFilePath = Path.Combine(localStorageDir, "appStorage.json");

                _cookiesLockStream ??= new FileStream(
                    cookiesFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                _appStorageLockStream ??= new FileStream(
                    appStorageFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                Debug.WriteLine("[MutexManager] Файлы RobloxCookies.dat и appStorage.json защищены на время сеанса.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MutexManager] Не удалось заблокировать локальный кэш кук Roblox: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Освобождает файлы кук Roblox сразу после закрытия окон, чтобы пользователь мог свободно играть в обычный Roblox.
    /// </summary>
    public void UnlockSharedLoginFiles()
    {
        lock (_syncLock)
        {
            try
            {
                _cookiesLockStream?.Dispose();
            }
            catch
            {
                // Игнорируем
            }
            finally
            {
                _cookiesLockStream = null;
            }

            try
            {
                _appStorageLockStream?.Dispose();
            }
            catch
            {
                // Игнорируем
            }
            finally
            {
                _appStorageLockStream = null;
            }

            Debug.WriteLine("[MutexManager] Файлы RobloxCookies.dat и appStorage.json освобождены.");
        }
    }

    private void MutexThreadWorker(
        string mutexName,
        ManualResetEventSlim releaseSignal,
        ManualResetEventSlim acquiredSignal)
    {
        Mutex? singletonEventMutex = null;
        Mutex? singletonSecondMutex = null;
        bool acquired = false;

        try
        {
            singletonEventMutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    acquired = singletonEventMutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }
            else
            {
                acquired = true;
            }

            try
            {
                singletonSecondMutex = new Mutex(initiallyOwned: true, name: "ROBLOX_singletonMutex", createdNew: out _);
            }
            catch
            {
                // Не критично
            }

            _isHoldingMutex = acquired;
            Debug.WriteLine($"[MutexManager] Мьютекс '{mutexName}' успешно захвачен: {acquired}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MutexManager] Ошибка при захвате мьютекса '{mutexName}': {ex.Message}");
            _isHoldingMutex = false;
        }
        finally
        {
            acquiredSignal.Set();
        }

        if (!acquired || singletonEventMutex is null)
        {
            singletonSecondMutex?.Dispose();
            singletonEventMutex?.Dispose();
            return;
        }

        try
        {
            releaseSignal.Wait();
        }
        finally
        {
            try
            {
                singletonEventMutex.ReleaseMutex();
            }
            catch
            {
                // Игнорируем при закрытии
            }

            singletonSecondMutex?.Dispose();
            singletonEventMutex.Dispose();
            _isHoldingMutex = false;
        }
    }

    private static void KillExistingRobloxProcessesIfNoneManaged()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1500);
                }
                catch
                {
                    // Игнорируем
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // Игнорируем
        }
    }

    private void OnRemoteConfigUpdated(RemoteConfig newConfig)
    {
        if (string.IsNullOrWhiteSpace(newConfig.MutexName))
            return;

        if (_isHoldingMutex && !string.Equals(_activeMutexName, newConfig.MutexName, StringComparison.Ordinal))
        {
            AcquireSingletonMutex();
        }
    }

    public void ReleaseMutex()
    {
        lock (_syncLock)
        {
            ReleaseCurrentMutexInternal();
        }
    }

    private void ReleaseCurrentMutexInternal()
    {
        if (_releaseSignal is not null)
        {
            _releaseSignal.Set();
            _mutexOwnerThread?.Join(TimeSpan.FromSeconds(2));
            _releaseSignal.Dispose();
            _acquiredSignal?.Dispose();

            _releaseSignal = null;
            _acquiredSignal = null;
            _mutexOwnerThread = null;
        }

        UnlockSharedLoginFiles();
        _isHoldingMutex = false;
        _activeMutexName = string.Empty;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        lock (_syncLock)
        {
            _isDisposed = true;
            _configService.ConfigUpdated -= OnRemoteConfigUpdated;
            ReleaseCurrentMutexInternal();
        }
    }
}