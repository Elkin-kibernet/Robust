using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RobloxWorker.Launcher.Services;

public sealed record LauncherLoginResult(string Token, string Username);

/// <summary>
/// Бесшовный вход через сайт: поднимает локальный HTTP-listener на 127.0.0.1,
/// открывает браузер на /api/launcher/auth?port=... и перехватывает токен.
/// TcpListener используется вместо HttpListener, чтобы не требовать прав администратора.
/// </summary>
public sealed class LauncherLoginService
{
    public string? LastLoginUrl { get; private set; }

    public async Task<LauncherLoginResult?> LoginAsync(
        string serverUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var normalized = LauncherConfigService.NormalizeServerUrl(serverUrl);

        return await Task.Run(() =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                LastLoginUrl = $"{normalized}/api/launcher/auth?port={port}";

                try
                {
                    Process.Start(new ProcessStartInfo(LastLoginUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Login] Не удалось открыть браузер: {ex.Message}");
                }

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!listener.Pending())
                    {
                        Thread.Sleep(60);
                        continue;
                    }

                    using var client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 4000;
                    using var stream = client.GetStream();

                    var requestLine = ReadRequestLine(stream);
                    if (string.IsNullOrWhiteSpace(requestLine))
                        continue;

                    var token = ExtractQueryParameter(requestLine, "token");
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        WriteResponse(stream, 404, "Ожидание входа", "Этот адрес используется лаунчером Robust.");
                        continue;
                    }

                    var username = ExtractQueryParameter(requestLine, "username");
                    WriteResponse(stream, 200, "Аккаунт подключён", BuildSuccessHtml(username, token));
                    return new LauncherLoginResult(token.Trim(), username?.Trim() ?? string.Empty);
                }

                return null;
            }
            finally
            {
                try { listener.Stop(); } catch { /* уже остановлен */ }
            }
        }, cancellationToken);
    }

    private static string? ReadRequestLine(NetworkStream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[1];
        while (memory.Length < 4096)
        {
            int read;
            try { read = stream.Read(buffer, 0, 1); }
            catch { return null; }

            if (read <= 0) break;
            if (buffer[0] == (byte)'\n') break;
            if (buffer[0] != (byte)'\r') memory.WriteByte(buffer[0]);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string? ExtractQueryParameter(string requestLine, string key)
    {
        // requestLine = "GET /callback?token=...&username=... HTTP/1.1"
        var firstSpace = requestLine.IndexOf(' ');
        var lastSpace = requestLine.LastIndexOf(' ');
        if (firstSpace < 0 || lastSpace <= firstSpace) return null;

        var target = requestLine.Substring(firstSpace + 1, lastSpace - firstSpace - 1);
        var question = target.IndexOf('?');
        if (question < 0) return null;

        var query = target.Substring(question + 1);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var name = Uri.UnescapeDataString(pair.Substring(0, separator));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair.Substring(separator + 1).Replace("+", " "));
        }

        return null;
    }

    private static void WriteResponse(NetworkStream stream, int statusCode, string title, string bodyHtml)
    {
        var statusText = statusCode == 200 ? "OK" : "Not Found";
        const string template = """
            <!DOCTYPE html>
            <html lang="ru">
            <head>
              <meta charset="utf-8"/>
              <meta name="viewport" content="width=device-width, initial-scale=1"/>
              <title>__TITLE__</title>
              <style>
                body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
                        background:#0d1117; color:#f0f6fc; font-family:'Segoe UI',sans-serif; }
                .card { max-width:460px; padding:34px 38px; background:#161b22; border:1px solid #2a3242;
                         border-radius:18px; text-align:center; box-shadow:0 18px 50px rgba(0,0,0,.45); }
                .dot { width:54px; height:54px; margin:0 auto 18px; border-radius:16px; background:#0f2e25;
                        border:1px solid #10b981; color:#10b981; font-size:26px; display:flex; align-items:center;
                        justify-content:center; }
                h1 { font-size:20px; margin:0 0 10px; }
                p { color:#9aa7b8; font-size:14px; line-height:1.6; margin:0; }
                code { display:inline-block; margin-top:14px; padding:8px 12px; border-radius:8px;
                        background:#0b1017; color:#10b981; font-size:12px; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="dot">✓</div>
                <h1>__TITLE__</h1>
                __BODY__
              </div>
            </body>
            </html>
            """;

        var html = template
            .Replace("__TITLE__", WebUtility.HtmlEncode(title))
            .Replace("__BODY__", bodyHtml);

        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        try
        {
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        catch
        {
            /* браузер уже закрыл соединение */
        }
    }

    private static string BuildSuccessHtml(string? username, string token)
    {
        var name = string.IsNullOrWhiteSpace(username) ? "аккаунт" : WebUtility.HtmlEncode(username);
        var masked = token.Length > 10 ? token.Substring(0, 6) + "…" + token.Substring(token.Length - 4) : token;
        return $"<p>Лаунчер получил ключ для <strong>{name}</strong>.<br/>Можно закрыть эту вкладку и вернуться в приложение.</p>" +
               $"<code>{WebUtility.HtmlEncode(masked)}</code>";
    }
}
