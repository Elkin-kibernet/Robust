using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RobloxWorker.Launcher.Models;

namespace RobloxWorker.Launcher.Services;

/// <summary>
/// Сервис локальной работы с аккаунтами Roblox и Анти-Фрод верификации:
/// - Шифрование куки .ROBLOSECURITY через Windows DPAPI.
/// - Автоматическое открытие настроек видимости статуса в игре (whoCanJoinMeInExperiences = AllUsers).
/// - Проверка реального присутствия аккаунта на сервере через официальный Roblox Presence API.
/// </summary>
public sealed class RobloxAuthService
{
    private const string UsersAuthUrl = "https://users.roblox.com/v1/users/authenticated";
    private const string AuthTicketUrl = "https://auth.roblox.com/v1/authentication-ticket";
    private const string LogoutCsrfFallbackUrl = "https://auth.roblox.com/v2/logout";
    private const string UserSettingsApiUrl = "https://apis.roblox.com/user-settings-api/v1/user-settings";
    private const string PresenceApiUrl = "https://presence.roblox.com/v1/presence/users";
    private const string RobloxReferer = "https://www.roblox.com/";
    private const string RobloxOrigin = "https://www.roblox.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("RobloxWorker_LocalCookieVault_v1");

    public static string ProtectCookie(string plainRobloSecurity)
    {
        if (string.IsNullOrWhiteSpace(plainRobloSecurity))
            throw new ArgumentException("Кука не может быть пустой.", nameof(plainRobloSecurity));

        var cleaned = CleanCookieValue(plainRobloSecurity);
        var plainBytes = Encoding.UTF8.GetBytes(cleaned);
        var encryptedBytes = ProtectedData.Protect(plainBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    public static string UnprotectCookie(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            throw new ArgumentException("Зашифрованная строка пуста.", nameof(encryptedBase64));

        var encryptedBytes = Convert.FromBase64String(encryptedBase64);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Проверяет валидность куки .ROBLOSECURITY, автоматически открывает настройки приватности для Анти-Фрода
    /// и возвращает готовую модель WorkerAccount.
    /// </summary>
    public async Task<WorkerAccount?> ValidateAndCreateAccountAsync(
        string rawRobloSecurity,
        string? proxyAddress = null,
        string? proxyUsername = null,
        string? proxyPassword = null,
        CancellationToken ct = default)
    {
        var cleanCookie = CleanCookieValue(rawRobloSecurity);
        using var client = CreateConfiguredHttpClient(cleanCookie, proxyAddress, proxyUsername, proxyPassword);

        try
        {
            using var response = await client.GetAsync(UsersAuthUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[RobloxAuth] Кука невалидна. Код ответа: {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var userInfo = JsonSerializer.Deserialize<RobloxAuthenticatedUserResponse>(json);

            if (userInfo is null || userInfo.Id <= 0)
                return null;

            // Автоматически включаем открытый статус присутствия в игре (AllUsers) для Анти-Фрода биржи
            bool privacyOk = await EnsurePublicPresencePrivacyInternalAsync(client, ct).ConfigureAwait(false);

            return new WorkerAccount
            {
                UserId = userInfo.Id,
                Username = userInfo.Name,
                DisplayName = userInfo.DisplayName,
                EncryptedRobloSecurity = ProtectCookie(cleanCookie),
                IsPrivacyConfigured = privacyOk,
                ProxyAddress = proxyAddress,
                ProxyUsername = proxyUsername,
                ProxyPassword = proxyPassword
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RobloxAuth] Ошибка при проверке аккаунта: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Получает одноразовый AuthTicket для запуска игры и убеждается, что приватность аккаунта открыта.
    /// </summary>
    public async Task<AuthTicketResult> GenerateAuthTicketAsync(WorkerAccount account, CancellationToken ct = default)
    {
        if (account is null)
            throw new ArgumentNullException(nameof(account));

        string plainCookie;
        try
        {
            plainCookie = UnprotectCookie(account.EncryptedRobloSecurity);
        }
        catch (CryptographicException)
        {
            return AuthTicketResult.Failure("Не удалось расшифровать куку (DPAPI). Добавьте аккаунт заново.");
        }

        using var client = CreateConfiguredHttpClient(
            plainCookie,
            account.ProxyAddress,
            account.ProxyUsername,
            account.ProxyPassword);

        try
        {
            var (csrfToken, csrfError) = await FetchCsrfTokenAsync(client, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(csrfToken))
            {
                return AuthTicketResult.Failure($"Не удалось получить X-CSRF-Token ({csrfError}).");
            }

            // Если у аккаунта ещё не были открыты настройки приватности — открываем их используя полученный CSRF-токен
            if (!account.IsPrivacyConfigured)
            {
                account.IsPrivacyConfigured = await ApplyPublicPrivacyWithCsrfAsync(client, csrfToken, ct).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, AuthTicketUrl)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
            request.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
            request.Headers.TryAddWithoutValidation("Origin", RobloxOrigin);
            request.Headers.Referrer = new Uri(RobloxReferer);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return AuthTicketResult.Failure($"Ошибка получения AuthTicket: HTTP {(int)response.StatusCode} ({errorBody})");
            }

            if (response.Headers.TryGetValues("rbx-authentication-ticket", out var ticketValues))
            {
                var ticket = ticketValues.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(ticket))
                {
                    return AuthTicketResult.Success(ticket);
                }
            }

            return AuthTicketResult.Failure("Сервер Roblox вернул 200 OK, но заголовок 'rbx-authentication-ticket' пуст.");
        }
        catch (Exception ex)
        {
            return AuthTicketResult.Failure($"Сетевая ошибка при получении билета: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет через официальный Roblox Presence API, находится ли аккаунт реально в целевой игре (PlaceId).
    /// </summary>
    public async Task<(bool IsVerifiedInGame, string? OfficialJobId, string StatusDetail)> VerifyUserInGamePresenceAsync(
        WorkerAccount account,
        long expectedPlaceId,
        CancellationToken ct = default)
    {
        if (account is null)
            return (false, null, "Аккаунт не задан");

        string plainCookie;
        try
        {
            plainCookie = UnprotectCookie(account.EncryptedRobloSecurity);
        }
        catch
        {
            return (false, null, "Ошибка DPAPI");
        }

        using var client = CreateConfiguredHttpClient(
            plainCookie,
            account.ProxyAddress,
            account.ProxyUsername,
            account.ProxyPassword);

        try
        {
            var payload = new RobloxPresenceRequest
            {
                UserIds = new List<long> { account.UserId }
            };

            var jsonBody = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, PresenceApiUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)response.StatusCode}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var presenceData = JsonSerializer.Deserialize<RobloxPresenceResponse>(responseJson);
            var userPresence = presenceData?.UserPresences?.FirstOrDefault(p => p.UserId == account.UserId);

            if (userPresence is null)
                return (false, null, "Пустой ответ Presence API");

            // userPresenceType == 2 означает "InGame" (внутри 3D-игры Roblox)
            if (userPresence.UserPresenceType != 2)
            {
                return (false, null, $"Не в игре (PresenceType: {userPresence.UserPresenceType})");
            }

            // Если PlaceId виден в ответе, сверяем его с целевым PlaceId или RootPlaceId
            if (userPresence.PlaceId.HasValue &&
                userPresence.PlaceId.Value != expectedPlaceId &&
                userPresence.RootPlaceId != expectedPlaceId)
            {
                return (false, userPresence.GameId, $"Чужой плейс (В игре: {userPresence.PlaceId}, ожидался: {expectedPlaceId})");
            }

            return (true, userPresence.GameId, "Подтверждён через Roblox Presence API");
        }
        catch (Exception ex)
        {
            return (false, null, $"Ошибка запроса Presence: {ex.Message}");
        }
    }

    /// <summary>
    /// Переключает настройки приватности Roblox (whoCanSeeMyOnlineStatus = AllUsers, whoCanJoinMeInExperiences = AllUsers),
    /// чтобы серверный Анти-Фрод всегда видел PlaceId и JobId запущенного аккаунта.
    /// </summary>
    private static async Task<bool> EnsurePublicPresencePrivacyInternalAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var (csrfToken, _) = await FetchCsrfTokenAsync(client, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(csrfToken))
                return false;

            return await ApplyPublicPrivacyWithCsrfAsync(client, csrfToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RobloxAuth] Ошибка настройки приватности аккаунта: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ApplyPublicPrivacyWithCsrfAsync(HttpClient client, string csrfToken, CancellationToken ct)
    {
        try
        {
            const string privacyPayload = "{\"whoCanSeeMyOnlineStatus\":\"AllUsers\",\"whoCanJoinMeInExperiences\":\"AllUsers\"}";

            using var request = new HttpRequestMessage(HttpMethod.Post, UserSettingsApiUrl)
            {
                Content = new StringContent(privacyPayload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrfToken);
            request.Headers.TryAddWithoutValidation("Origin", RobloxOrigin);
            request.Headers.Referrer = new Uri(RobloxReferer);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            Debug.WriteLine($"[RobloxAuth] Настройка открытого статуса (AllUsers) завершена с кодом: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string? Token, string? ErrorDetail)> FetchCsrfTokenAsync(HttpClient client, CancellationToken ct)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, AuthTicketUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        })
        {
            request.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
            request.Headers.TryAddWithoutValidation("Origin", RobloxOrigin);
            request.Headers.Referrer = new Uri(RobloxReferer);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Headers.TryGetValues("x-csrf-token", out var tokens))
            {
                var token = tokens.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(token))
                    return (token, null);
            }
        }

        using (var fallbackRequest = new HttpRequestMessage(HttpMethod.Post, LogoutCsrfFallbackUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        })
        {
            fallbackRequest.Headers.TryAddWithoutValidation("Origin", RobloxOrigin);
            fallbackRequest.Headers.Referrer = new Uri(RobloxReferer);

            using var response = await client.SendAsync(fallbackRequest, ct).ConfigureAwait(false);
            if (response.Headers.TryGetValues("x-csrf-token", out var tokens))
            {
                var token = tokens.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(token))
                    return (token, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (null, $"HTTP {(int)response.StatusCode}: {body}");
        }
    }

    private static HttpClient CreateConfiguredHttpClient(
        string cleanRobloSecurity,
        string? proxyAddress,
        string? proxyUsername,
        string? proxyPassword)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12)
        };

        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            var webProxy = new WebProxy(proxyAddress.Trim());
            if (!string.IsNullOrWhiteSpace(proxyUsername))
            {
                webProxy.Credentials = new NetworkCredential(proxyUsername.Trim(), proxyPassword ?? string.Empty);
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cleanRobloSecurity}");
        return client;
    }

    private static string CleanCookieValue(string rawInput)
    {
        var trimmed = rawInput.Trim().Trim('"', '\'');
        const string prefix = ".ROBLOSECURITY=";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..].Trim();
        }

        return trimmed;
    }
}