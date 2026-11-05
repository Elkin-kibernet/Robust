using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RobloxWorker.Launcher.Services;

public enum RobloxPathSource
{
    Registry,
    VersionsFolder,
    Bootstrapper,
    NotFound
}

public sealed class RobloxPathResolution
{
    public string? PlayerExecutablePath { get; init; }
    public string? BootstrapperPath { get; init; }
    public RobloxPathSource Source { get; init; }
    public bool HasPlayer =>
        !string.IsNullOrWhiteSpace(PlayerExecutablePath) && File.Exists(PlayerExecutablePath);
}

/// <summary>
/// Гарантированный поиск актуального RobloxPlayerBeta.exe.
/// Путь не кэшируется: Roblox меняет папку version-* после каждого обновления,
/// поэтому резолвер вызывается заново перед каждым запуском окна.
/// </summary>
public static class RobloxPathResolver
{
    public const string PlayerExecutableName = "RobloxPlayerBeta.exe";
    public const string BootstrapperExecutableName = "RobloxPlayerLauncher.exe";
    public const string InstallerExecutableName = "RobloxPlayerInstaller.exe";

    private const string PlayerProtocolKey = @"Software\Classes\roblox-player\shell\open\command";
    private const string ClassesProtocolKey = @"roblox-player\shell\open\command";

    /// <summary>
    /// Трёхуровневый поиск:
    /// 1) реестр Windows (ключ протокола roblox-player);
    /// 2) сканирование папок Versions с выбором самой свежей version-*;
    /// 3) официальный апдейтер RobloxPlayerLauncher.exe.
    /// </summary>
    public static RobloxPathResolution Resolve()
    {
        // ---- Метод 1: реестр Windows ----
        var registryCommands = ReadRegistryCommands();
        foreach (var command in registryCommands)
        {
            var candidate = ExtractExecutablePath(command);
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;

            if (IsPlayerExecutable(candidate))
            {
                return new RobloxPathResolution
                {
                    PlayerExecutablePath = candidate,
                    Source = RobloxPathSource.Registry
                };
            }
        }

        // ---- Метод 2: динамический поиск по папкам Versions ----
        var latestPlayer = FindLatestPlayerExecutable();
        if (!string.IsNullOrWhiteSpace(latestPlayer))
        {
            return new RobloxPathResolution
            {
                PlayerExecutablePath = latestPlayer,
                Source = RobloxPathSource.VersionsFolder
            };
        }

        // ---- Метод 3: автовосстановление через официальный апдейтер ----
        var bootstrapper = FindBootstrapper(registryCommands);
        return new RobloxPathResolution
        {
            BootstrapperPath = bootstrapper,
            Source = bootstrapper != null ? RobloxPathSource.Bootstrapper : RobloxPathSource.NotFound
        };
    }

    public static string? FindLatestPlayerExecutable()
    {
        var bestPath = null as string;
        var bestWriteTime = DateTime.MinValue;

        foreach (var root in GetVersionsRoots())
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var dir in Directory.GetDirectories(root, "version-*", SearchOption.TopDirectoryOnly))
                {
                    var exePath = Path.Combine(dir, PlayerExecutableName);
                    if (!File.Exists(exePath))
                        continue;

                    DateTime writeTime;
                    try
                    {
                        writeTime = Directory.GetLastWriteTimeUtc(dir);
                    }
                    catch
                    {
                        writeTime = File.GetLastWriteTimeUtc(exePath);
                    }

                    if (writeTime > bestWriteTime)
                    {
                        bestWriteTime = writeTime;
                        bestPath = exePath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RobloxPathResolver] Ошибка сканирования '{root}': {ex.Message}");
            }
        }

        return bestPath;
    }

    /// <summary>
    /// Ищет официальный апдейтер Roblox: RobloxPlayerLauncher.exe,
    /// а в свежих установках — RobloxPlayerInstaller.exe.
    /// </summary>
    public static string? FindBootstrapper(IEnumerable<string>? registryCommands = null)
    {
        var bootstrapperNames = new[] { BootstrapperExecutableName, InstallerExecutableName };

        // Если реестр прямо указывает на апдейтер — используем его.
        foreach (var command in registryCommands ?? ReadRegistryCommands())
        {
            var candidate = ExtractExecutablePath(command);
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;
            if (bootstrapperNames.Any(n =>
                    string.Equals(Path.GetFileName(candidate), n, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        // Апдейтер в корне папки Roblox.
        foreach (var root in GetVersionsRoots())
        {
            var baseDirectory = Path.GetDirectoryName(root) ?? root;
            foreach (var name in bootstrapperNames)
            {
                foreach (var probe in new[]
                         {
                             Path.Combine(baseDirectory, name),
                             Path.Combine(root, name)
                         })
                {
                    if (File.Exists(probe))
                        return probe;
                }
            }
        }

        // Апдейтер внутри папок версий — начинаем с самой свежей version-*.
        var versionDirectories = new List<(string Directory, DateTime WriteTime)>();
        foreach (var root in GetVersionsRoots())
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var dir in Directory.GetDirectories(root, "version-*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        versionDirectories.Add((dir, Directory.GetLastWriteTimeUtc(dir)));
                    }
                    catch
                    {
                        versionDirectories.Add((dir, DateTime.MinValue));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RobloxPathResolver] Ошибка поиска апдейтера в '{root}': {ex.Message}");
            }
        }

        foreach (var (dir, _) in versionDirectories.OrderByDescending(v => v.WriteTime))
        {
            foreach (var name in bootstrapperNames)
            {
                var probe = Path.Combine(dir, name);
                if (File.Exists(probe))
                    return probe;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetVersionsRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "Roblox", "Versions");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Roblox", "Versions");
    }

    private static List<string> ReadRegistryCommands()
    {
        var commands = new List<string>();

        TryReadRegistry(Registry.CurrentUser, PlayerProtocolKey, commands);
        TryReadRegistry(Registry.ClassesRoot, ClassesProtocolKey, commands);
        TryReadRegistry(Registry.LocalMachine, PlayerProtocolKey, commands);

        return commands;
    }

    private static void TryReadRegistry(RegistryKey root, string subKey, List<string> commands)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            var value = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(value))
                commands.Add(value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RobloxPathResolver] Не удалось прочитать реестр '{subKey}': {ex.Message}");
        }
    }

    /// <summary>
    /// Извлекает путь к .exe из командной строки вида
    /// "C:\...\RobloxPlayerBeta.exe" "%1" или C:\...\RobloxPlayerBeta.exe %1.
    /// </summary>
    public static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var trimmed = commandLine.Trim();

        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
                return trimmed.Substring(1, closingQuote - 1).Trim();
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var path = trimmed.Substring(0, exeIndex + 4).Trim();
            return path.Trim('"');
        }

        return null;
    }

    private static bool IsPlayerExecutable(string path)
    {
        return string.Equals(
            Path.GetFileName(path),
            PlayerExecutableName,
            StringComparison.OrdinalIgnoreCase);
    }
}
