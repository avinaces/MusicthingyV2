using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Musicthingy.Services.YouTube;

/// <summary>
/// Google/YouTube OAuth 2.0 device-code flow ("TVs and Limited Input devices").
///
/// Endpoints, scope, grant type and the special User-Agent are exactly what
/// ytmusicapi uses (verified against ytmusicapi/constants.py and
/// ytmusicapi/auth/oauth/credentials.py):
///
///   device code : https://www.youtube.com/o/oauth2/device/code
///   token       : https://oauth2.googleapis.com/token
///   scope       : https://www.googleapis.com/auth/youtube
///   grant type  : http://oauth.net/grant_type/device/1.0   (legacy device grant)
///   user agent  : "…Firefox/88.0 Cobalt/Version"           (YouTube expects
///                 a Cobalt-style UA for this flow)
///
/// The app uses a user-created Google Cloud OAuth client of type
/// "TVs and Limited Input devices" (the same setup ytmusicapi requires since
/// November 2024) — client id/secret are configurable in Settings.
/// </summary>
public static class GoogleDeviceFlowClient
{
    public const string DeviceCodeUrl = "https://www.youtube.com/o/oauth2/device/code";
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string Scope = "https://www.googleapis.com/auth/youtube";

    // Google's device flow validates the UA; ytmusicapi appends "Cobalt/Version".
    public const string OAuthUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0 Cobalt/Version";

    // yt-dlp removed its own OAuth login ("Login with OAuth is no longer
    // supported"); this device-flow client is the maintained path (2026).

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OAuthUserAgent);
        return client;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Result models
    // ─────────────────────────────────────────────────────────────────────

    public sealed class DeviceCode
    {
        public string DeviceCodeValue { get; init; } = "";
        public string UserCode { get; init; } = "";
        public string VerificationUrl { get; init; } = "";
        public int ExpiresInSeconds { get; init; }
        public int IntervalSeconds { get; init; } = 5;

        /// <summary>Convenience URL that pre-fills the user code.</summary>
        public string VerificationUrlWithCode =>
            string.IsNullOrEmpty(UserCode) ? VerificationUrl
            : $"{VerificationUrl.TrimEnd('/')}?user_code={Uri.EscapeDataString(UserCode)}";
    }

    public sealed class TokenResponse
    {
        public string AccessToken { get; init; } = "";
        public string? RefreshToken { get; init; }
        public int ExpiresIn { get; init; }
        public string? Scope { get; init; }
        public string TokenType { get; init; } = "Bearer";
    }

    public sealed class DeviceFlowException : Exception
    {
        public string ErrorCode { get; }
        public DeviceFlowException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Flow steps
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Step 1: request a user code the user can enter at google.com.</summary>
    public static async Task<DeviceCode> RequestDeviceCodeAsync(string clientId, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(DeviceCodeUrl, content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        ThrowIfOAuthClientError(response.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (response.StatusCode == HttpStatusCode.OK && root.TryGetProperty("device_code", out var dc))
        {
            return new DeviceCode
            {
                DeviceCodeValue = dc.GetString() ?? "",
                UserCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() ?? "" : "",
                VerificationUrl = root.TryGetProperty("verification_url", out var vu) ? vu.GetString() ?? "" : "https://www.google.com/device",
                ExpiresInSeconds = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int e) ? e : 1800,
                IntervalSeconds = root.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out int i) ? Math.Max(1, i) : 5
            };
        }

        throw new DeviceFlowException("code_request_failed", $"Device-code request failed: {(int)response.StatusCode}");
    }

    /// <summary>
    /// Step 2: poll the token endpoint until the user approves (or the code
    /// expires). Handles authorization_pending / slow_down / expired_token.
    /// </summary>
    public static async Task<TokenResponse> PollForTokenAsync(
        string clientId, string clientSecret, DeviceCode code, CancellationToken ct = default)
    {
        int interval = code.IntervalSeconds;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, code.ExpiresInSeconds - 10));

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var (status, body) = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "http://oauth.net/grant_type/device/1.0",
                ["code"] = code.DeviceCodeValue
            }, ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
            switch (error)
            {
                case "authorization_pending":
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                    continue;

                case "slow_down":
                    interval = Math.Min(interval * 2, 30);
                    await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                    continue;

                case "expired_token":
                    throw new DeviceFlowException("expired_token", "The sign-in code expired. Please try again.");

                case "access_denied":
                    throw new DeviceFlowException("access_denied", "Sign-in was cancelled on the Google page.");

                case null when status == HttpStatusCode.OK && root.TryGetProperty("access_token", out var at):
                    return new TokenResponse
                    {
                        AccessToken = at.GetString() ?? "",
                        RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                        ExpiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int e) ? e : 3600,
                        Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
                    };
            }

            ThrowIfOAuthClientError(status, body);
            throw new DeviceFlowException(error ?? "token_error", $"Token request failed: {(int)status} {error}");
        }

        throw new DeviceFlowException("timeout", "Timed out waiting for sign-in approval.");
    }

    /// <summary>Refresh an access token with the stored refresh token.</summary>
    public static async Task<TokenResponse?> RefreshTokenAsync(
        string clientId, string clientSecret, string refreshToken, CancellationToken ct = default)
    {
        try
        {
            var (status, body) = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }, ct);

            if (status != HttpStatusCode.OK)
            {
                ThrowIfOAuthClientError(status, body, throwAlways: false);
                Debug.WriteLine($"[OAuth] Refresh failed: {(int)status} {body}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at)) return null;

            return new TokenResponse
            {
                AccessToken = at.GetString() ?? "",
                ExpiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int e) ? e : 3600,
                Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
            };
        }
        catch (DeviceFlowException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OAuth] Refresh error: {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<(HttpStatusCode, string)> PostTokenAsync(
        Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(TokenUrl, content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, body);
    }

    private static void ThrowIfOAuthClientError(HttpStatusCode status, string body, bool throwAlways = true)
    {
        if (status != HttpStatusCode.Unauthorized && status != HttpStatusCode.BadRequest) return;

        string? error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { }

        if (error == "unauthorized_client")
            throw new DeviceFlowException(error, "Token/client mismatch. Re-create the sign-in.");
        if (error == "invalid_client")
            throw new DeviceFlowException(error,
                "Invalid OAuth client id/secret. Check the client ID & secret in Settings " +
                "and that 'YouTube Data API v3' is enabled in your Google Cloud project.");
        if (error == "invalid_grant")
            throw new DeviceFlowException(error, "The stored refresh token is no longer valid. Please sign in again.");

        if (throwAlways && (int)status >= 400)
            throw new DeviceFlowException(error ?? "http_error", $"OAuth request failed: {(int)status}");
    }
}
