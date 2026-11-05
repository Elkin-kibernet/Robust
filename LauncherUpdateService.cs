using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxWorker.Launcher.Services;

public sealed class LauncherVersionInfo
{
    public string Version { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// Проверка /api/launcher/version и ручное автообновление без захода на сайт.
/// </summary>
public sealed class LauncherUpdateService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<LauncherVersionInfo?> GetLatestVersionAsync(string serverUrl, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = LauncherConfigService.NormalizeServerUrl(serverUrl);
            using var response = await _httpClient.GetAsync(baseUrl + "/api/launcher/version", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? string.Empty) : string.Empty;
            if (string.IsNullOrWhiteSpace(version)) return null;

            var downloadUrl = root.TryGetProperty("download_url", out var d) ? (d.GetString() ?? string.Empty) : string.Empty;
            if (string.IsNullOrWhiteSpace(downloadUrl))
                downloadUrl = baseUrl + "/download/launcher";
            else if (downloadUrl.StartsWith("/", StringComparison.Ordinal))
                downloadUrl = baseUrl + downloadUrl;

            var notes = root.TryGetProperty("notes", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
            return new LauncherVersionInfo
            {
                Version = version.Trim(),
                DownloadUrl = downloadUrl,
                Notes = notes
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Проверка версии не удалась: {ex.Message}");
            return null;
        }
    }

    public static bool IsNewer(string remoteVersion, string currentVersion)
    {
        var remote = ParseVersion(remoteVersion);
        var current = ParseVersion(currentVersion);
        var length = Math.Max(remote.Length, current.Length);
        for (var i = 0; i < length; i += 1)
        {
            var r = i < remote.Length ? remote[i] : 0;
            var c = i < current.Length ? current[i] : 0;
            if (r > c) return true;
            if (r < c) return false;
        }
        return false;
    }

    private static int[] ParseVersion(string value)
    {
        return (value ?? string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(new string(part.TakeWhile(char.IsDigit).ToArray()), out var number) ? number : 0)
            .Take(4)
            .ToArray();
    }

    /// <summary>
    /// Скачивает новый билд, готовит скрипт замены и перезапускает приложение.
    /// </summary>
    public async Task DownloadAndApplyAsync(
        LauncherVersionInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var targetPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу лаунчера.");

        if (string.Equals(Path.GetFileName(targetPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Автообновление доступно только для собранной программы Robust.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "RobloxWorkerUpdate");
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
        Directory.CreateDirectory(tempDirectory);

        var downloadPath = Path.Combine(tempDirectory, "downloaded.bin");
        using (var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(downloadPath);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total > 0) progress?.Report(Math.Round(written * 100d / total, 1));
            }
        }

        var newExecutable = ExtractExecutableIfNeeded(downloadPath, tempDirectory)
            ?? downloadPath;

        var scriptPath = Path.Combine(tempDirectory, "apply_update.ps1");
        var script = BuildUpdateScript(targetPath, newExecutable);
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(startInfo);
    }

    private static string? ExtractExecutableIfNeeded(string downloadedPath, string tempDirectory)
    {
        try
        {
            using var stream = File.OpenRead(downloadedPath);
            if (stream.Length < 4) return null;

            var signature = new byte[4];
            stream.Read(signature, 0, 4);
            if (signature[0] != 0x50 || signature[1] != 0x4B) return null;

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.Entries
                .Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();
            if (entry is null) return null;

            var destination = Path.Combine(tempDirectory, entry.Name);
            entry.ExtractToFile(destination, overwrite: true);
            return destination;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Не удалось распаковать архив: {ex.Message}");
            return null;
        }
    }

    private static string BuildUpdateScript(string targetPath, string sourcePath)
    {
        static string Escape(string value) => value.Replace("'", "''");

        const string template = """
            $ErrorActionPreference = 'SilentlyContinue'
            $targetPid = __PID__
            $target = '__TARGET__'
            $source = '__SOURCE__'

            while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) {
                Start-Sleep -Milliseconds 400
            }

            for ($i = 0; $i -lt 120; $i++) {
                try {
                    Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
                    break
                } catch {
                    Start-Sleep -Milliseconds 500
                }
            }

            Start-Process -FilePath $target
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force
            """;

        return template
            .Replace("__PID__", Environment.ProcessId.ToString())
            .Replace("__TARGET__", Escape(targetPath))
            .Replace("__SOURCE__", Escape(sourcePath));
    }

    public void Dispose() => _httpClient.Dispose();
}
