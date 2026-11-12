using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace Musicthingy.Services.Accounts;

/// <summary>
/// Multi-account, multi-platform account store. Owns all signed-in accounts
/// (YouTube: OAuth or cookies; Spotify: daemon pairing state) and is the
/// single source of truth for what gets pushed into YouTubeResolverService.
///
/// Persistence: SecureStorage (Android Keystore / Windows DPAPI) with a
/// Preferences fallback for devices where SecureStorage is unavailable.
///
/// Multiple accounts per platform are supported simultaneously; each
/// platform has exactly one "active" account used for requests.
/// </summary>
public class AccountManager
{
    // ─────────────────────────────────────────────────────────────────────
    // Self-healing instantiation (zip-only installs)
    // ─────────────────────────────────────────────────────────────────────
    // This zip ships no MauiProgram.cs, so the account store may not be
    // registered in DI. Consumers call Resolve(services): DI instance when
    // one is registered (Task-26 style), otherwise this shared singleton.
    // Either way every caller sees the SAME instance and the store works
    // with zero DI changes.
    private static readonly Lazy<AccountManager> _default = new(() => new AccountManager());

    /// <summary>Shared fallback instance when nothing is registered in DI.</summary>
    public static AccountManager Default => _default.Value;

    /// <summary>DI instance if registered, otherwise <see cref="Default"/>.</summary>
    public static AccountManager Resolve(IServiceProvider? services) =>
        services?.GetService<AccountManager>() ?? Default;

    /// <summary>Idempotent InitializeAsync — safe to await before every use.</summary>
    public Task EnsureInitializedAsync() =>
        _initialized ? Task.CompletedTask : InitializeAsync();

    private const string StorageKey = "mt_accounts_v1";
    private const string PrefsFallbackKey = "mt_accounts_v1_fallback";

    // Legacy preference keys written by the pre-account settings system.
    private const string LegacyCookieKey = "InnerTubeCookie";
    private const string LegacyVisitorDataKey = "VisitorData";
    private const string LegacyDataSyncIdKey = "DataSyncId";
    private const string LegacyPoTokenKey = "PoToken";
    private const string LegacyAccountNameKey = "AccountName";
    private const string LegacyAccountEmailKey = "AccountEmail";
    private const string LegacyChannelHandleKey = "AccountChannelHandle";

    // OAuth client credentials (written by YouTubeOAuthClientIdSetting etc.)
    public const string OAuthClientIdPrefsKey = "YouTubeOAuthClientId";
    public const string OAuthClientSecretPrefsKey = "YouTubeOAuthClientSecret";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private bool _initialized;
    private bool _usingSecureStorage = true;

    public ObservableCollection<AccountRecord> Accounts { get; } = new();

    /// <summary>AccountId of the active account, per platform.</summary>
    public Dictionary<MusicPlatform, string?> ActiveAccountIds { get; private set; } = new();

    /// <summary>Fired whenever accounts or the active selection change.</summary>
    public event Action? AccountsChanged;

    // ─────────────────────────────────────────────────────────────────────
    // Initialization
    // ─────────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            List<AccountRecord>? loaded = null;
            Dictionary<MusicPlatform, string?>? active = null;

            // 1. Try SecureStorage.
            try
            {
                string? json = await SecureStorage.GetAsync(StorageKey);
                if (!string.IsNullOrWhiteSpace(json))
                    (loaded, active) = DeserializeStore(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Accounts] SecureStorage read failed ({ex.Message}); using Preferences fallback");
                _usingSecureStorage = false;
            }

            // 2. Preferences fallback (also used when SecureStorage has never
            //    been written on a device that silently no-ops it).
            if (loaded == null)
            {
                string? json = Preferences.Default.Get(PrefsFallbackKey, (string?)null);
                if (!string.IsNullOrWhiteSpace(json))
                    (loaded, active) = DeserializeStore(json);
            }

            if (loaded != null)
            {
                foreach (var acc in loaded) Accounts.Add(acc);
                ActiveAccountIds = active ?? new Dictionary<MusicPlatform, string?>();
            }

            // 3. One-time migration from the legacy single-account settings.
            if (!Accounts.Any(a => a.Platform == MusicPlatform.YouTube))
            {
                var migrated = TryMigrateLegacyYouTube();
                if (migrated != null)
                {
                    Accounts.Add(migrated);
                    ActiveAccountIds[MusicPlatform.YouTube] = migrated.Id;
                    await SaveAsync();
                    Debug.WriteLine($"[Accounts] Migrated legacy YouTube auth as account '{migrated.DisplayName}'");
                }
            }

            // 4. Push the active YouTube account into the resolver. From this
            //    moment the AccountManager owns YouTube auth state; the legacy
            //    hidden settings route their pushes through AccountsAuthBridge
            //    and become no-ops.
            PushActiveYouTubeToResolver();
            AccountsAuthBridge.AccountManagerOwnsYouTube = true;

            // 5. Keep captured cookie sessions alive: YouTube rotates the
            //    short-lived __Secure-*PSIDTS cookies on nearly every innertube
            //    response. Persist the fresh values into the active cookie
            //    account so web-login sessions do not expire after a few hours.
            YouTubeResolverService.SessionCookieRefreshed += OnSessionCookieRefreshed;
            _initialized = true;

            Debug.WriteLine($"[Accounts] Initialized with {Accounts.Count} account(s)");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Queries
    // ─────────────────────────────────────────────────────────────────────

    public IEnumerable<AccountRecord> GetAccounts(MusicPlatform platform) =>
        Accounts.Where(a => a.Platform == platform).ToList();

    public int Count(MusicPlatform platform) => Accounts.Count(a => a.Platform == platform);

    public AccountRecord? GetActive(MusicPlatform platform)
    {
        if (ActiveAccountIds.TryGetValue(platform, out string? id) && !string.IsNullOrEmpty(id))
        {
            var acc = Accounts.FirstOrDefault(a => a.Id == id);
            if (acc != null) return acc;
        }
        return Accounts.FirstOrDefault(a => a.Platform == platform);
    }

    public AccountRecord? GetById(string accountId) => Accounts.FirstOrDefault(a => a.Id == accountId);

    // ─────────────────────────────────────────────────────────────────────
    // Mutations
    // ─────────────────────────────────────────────────────────────────────

    public async Task AddAccountAsync(AccountRecord account, bool makeActive = true)
    {
        if (Accounts.Any(a => a.Id == account.Id)) return;
        Accounts.Add(account);
        if (makeActive || Count(account.Platform) == 1)
            ActiveAccountIds[account.Platform] = account.Id;

        await SaveAsync();

        if (account.Platform == MusicPlatform.YouTube)
            PushActiveYouTubeToResolver();

        AccountsChanged?.Invoke();
    }

    public async Task RemoveAccountAsync(AccountRecord account)
    {
        bool wasActive = ActiveAccountIds.TryGetValue(account.Platform, out string? id) && id == account.Id;
        Accounts.Remove(account);
        if (wasActive)
        {
            var next = Accounts.FirstOrDefault(a => a.Platform == account.Platform);
            ActiveAccountIds[account.Platform] = next?.Id;
        }

        await SaveAsync();

        if (account.Platform == MusicPlatform.YouTube)
            PushActiveYouTubeToResolver();

        AccountsChanged?.Invoke();
    }

    public async Task SetActiveAsync(MusicPlatform platform, string accountId)
    {
        var acc = Accounts.FirstOrDefault(a => a.Id == accountId && a.Platform == platform);
        if (acc == null) return;

        ActiveAccountIds[platform] = accountId;
        await SaveAsync();

        if (platform == MusicPlatform.YouTube)
            PushActiveYouTubeToResolver();

        AccountsChanged?.Invoke();
    }

    public async Task UpdateAccountAsync(AccountRecord account)
    {
        // AccountRecord is a reference inside Accounts; just persist.
        await SaveAsync();
        if (account.Platform == MusicPlatform.YouTube &&
            GetActive(MusicPlatform.YouTube)?.Id == account.Id)
        {
            // Re-push so refreshed tokens are picked up.
            PushActiveYouTubeToResolver();
        }
    }

    public async Task ClearPlatformAsync(MusicPlatform platform)
    {
        foreach (var acc in Accounts.Where(a => a.Platform == platform).ToList())
            Accounts.Remove(acc);
        ActiveAccountIds[platform] = null;

        await SaveAsync();

        if (platform == MusicPlatform.YouTube)
        {
            YouTubeResolverService.Cookies = null;
            YouTubeResolverService.VisitorData = null;
            YouTubeResolverService.DataSyncId = null;
            YouTubeResolverService.PoToken = null;
            YouTubeResolverService.OAuthTokenProvider = null;
        }

        AccountsChanged?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────
    // YouTube auth plumbing
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the active YouTube account's auth into the resolver's static
    /// state. Cookie accounts populate the legacy cookie/visitor/poToken
    /// fields; OAuth accounts clear them and install a token provider that
    /// transparently refreshes the access token when it is about to expire.
    /// </summary>
    public void PushActiveYouTubeToResolver()
    {
        var active = GetActive(MusicPlatform.YouTube);
        var data = active?.DeserializeAuth<YouTubeAuthData>() ?? new YouTubeAuthData();

        if (active != null && active.YouTubeKind == YouTubeAuthKind.Cookies && data.HasCookies)
        {
            YouTubeResolverService.Cookies = data.Cookie;
            YouTubeResolverService.VisitorData = data.VisitorData;
            YouTubeResolverService.DataSyncId = data.DataSyncId;
            YouTubeResolverService.PoToken = data.PoToken;
            YouTubeResolverService.OAuthTokenProvider = null;
            Debug.WriteLine($"[Accounts] YouTube auth: cookies ('{active.DisplayName}')");
        }
        else if (active != null && active.YouTubeKind == YouTubeAuthKind.OAuth && data.HasOAuth)
        {
            YouTubeResolverService.Cookies = null;
            YouTubeResolverService.VisitorData = null;
            YouTubeResolverService.DataSyncId = null;
            YouTubeResolverService.PoToken = null;

            var account = active;
            YouTubeResolverService.OAuthTokenProvider = () => GetFreshYouTubeAccessTokenAsync(account);
            Debug.WriteLine($"[Accounts] YouTube auth: OAuth ('{active.DisplayName}')");
        }
        else
        {
            YouTubeResolverService.Cookies = null;
            YouTubeResolverService.VisitorData = null;
            YouTubeResolverService.DataSyncId = null;
            YouTubeResolverService.PoToken = null;
            YouTubeResolverService.OAuthTokenProvider = null;
            Debug.WriteLine("[Accounts] YouTube auth: anonymous (no account)");
        }
    }

    /// <summary>
    /// Returns a valid access token for the given OAuth account, refreshing
    /// (and persisting) it when fewer than 60 seconds remain. Mirrors
    /// ytmusicapi's RefreshingToken behaviour.
    /// </summary>
    public async Task<string?> GetFreshYouTubeAccessTokenAsync(AccountRecord account)
    {
        var data = account.DeserializeAuth<YouTubeAuthData>() ?? new YouTubeAuthData();
        if (string.IsNullOrEmpty(data.RefreshToken)) return null;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(data.AccessToken) && data.AccessTokenExpiresAtUnix - now > 60)
            return data.AccessToken;

        await _refreshLock.WaitAsync();
        try
        {
            // Re-read in case another thread refreshed while we waited.
            data = account.DeserializeAuth<YouTubeAuthData>() ?? new YouTubeAuthData();
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(data.AccessToken) && data.AccessTokenExpiresAtUnix - now > 60)
                return data.AccessToken;

            string? refreshToken = data.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                Debug.WriteLine("[Accounts] OAuth refresh skipped: no refresh token on re-read");
                return null;
            }

            string clientId = Preferences.Default.Get(OAuthClientIdPrefsKey, "");
            string clientSecret = Preferences.Default.Get(OAuthClientSecretPrefsKey, "");
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                Debug.WriteLine("[Accounts] OAuth refresh skipped: no client id/secret configured");
                return data.AccessToken; // stale token is better than nothing
            }

            var refreshed = await YouTube.GoogleDeviceFlowClient.RefreshTokenAsync(clientId, clientSecret, refreshToken);
            if (refreshed == null)
            {
                Debug.WriteLine("[Accounts] OAuth refresh failed");
                return data.AccessToken;
            }

            data.AccessToken = refreshed.AccessToken;
            data.AccessTokenExpiresAtUnix = now + refreshed.ExpiresIn;
            data.Scope = refreshed.Scope ?? data.Scope;
            account.SerializeAuth(data);
            await SaveAsync();
            Debug.WriteLine("[Accounts] OAuth access token refreshed");
            return data.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────

    private long _lastCookiePersistUnix;

    /// <summary>
    /// Fired by the resolver whenever YouTube rotates a __Secure-*PSIDTS
    /// cookie. Updates the active cookie account's cookie string in memory
    /// (immediately) and in SecureStorage (throttled to one write per minute).
    /// </summary>
    private async void OnSessionCookieRefreshed(string name, string value)
    {
        try
        {
            var active = GetActive(MusicPlatform.YouTube);
            if (active == null || active.YouTubeKind != YouTubeAuthKind.Cookies) return;

            var data = active.DeserializeAuth<YouTubeAuthData>() ?? new YouTubeAuthData();
            if (!data.HasCookies) return;

            string updated = ReplaceCookieValue(data.Cookie!, name, value);
            if (updated == data.Cookie) return;

            data.Cookie = updated;
            active.SerializeAuth(data);

            // Keep the resolver's live state (and YoutubeExplode cookie client)
            // in sync immediately — the Cookies setter also invalidates caches.
            YouTubeResolverService.Cookies = data.Cookie;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - _lastCookiePersistUnix < 60)
                return; // memory is already fresh; persist at most once per minute
            _lastCookiePersistUnix = now;

            await SaveAsync();
            Debug.WriteLine($"[Accounts] Session cookie '{name}' refreshed and persisted");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Accounts] cookie refresh persist failed: {ex.Message}");
        }
    }

    /// <summary>Replaces (or appends) one cookie inside a "k=v; k2=v2" string.</summary>
    internal static string ReplaceCookieValue(string cookieString, string name, string value)
    {
        var parts = new List<string>();
        bool replaced = false;
        foreach (string raw in cookieString.Split(';'))
        {
            string trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            int eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{name}={value}");
                replaced = true;
            }
            else
            {
                parts.Add(trimmed);
            }
        }

        if (!replaced) parts.Add($"{name}={value}");
        return string.Join("; ", parts);
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var payload = new AccountStorePayload
            {
                Accounts = Accounts.ToList(),
                ActiveAccountIds = ActiveAccountIds.ToDictionary(
                    kv => kv.Key.ToString(), kv => kv.Value)
            };
            string json = JsonSerializer.Serialize(payload);

            if (_usingSecureStorage)
            {
                try
                {
                    await SecureStorage.SetAsync(StorageKey, json);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Accounts] SecureStorage write failed ({ex.Message}); switching to Preferences");
                    _usingSecureStorage = false;
                }
            }

            Preferences.Default.Set(PrefsFallbackKey, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Accounts] Save failed: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static (List<AccountRecord>?, Dictionary<MusicPlatform, string?>?) DeserializeStore(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<AccountStorePayload>(json);
            if (payload?.Accounts == null) return (null, null);

            var active = new Dictionary<MusicPlatform, string?>();
            if (payload.ActiveAccountIds != null)
            {
                foreach (var kv in payload.ActiveAccountIds)
                {
                    if (Enum.TryParse<MusicPlatform>(kv.Key, out var platform))
                        active[platform] = kv.Value;
                }
            }
            return (payload.Accounts, active);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Accounts] Store deserialize failed: {ex.Message}");
            return (null, null);
        }
    }

    private class AccountStorePayload
    {
        public List<AccountRecord> Accounts { get; set; } = new();
        public Dictionary<string, string?> ActiveAccountIds { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Legacy migration
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the pre-account auth fields straight from Preferences (independent
    /// of whether SettingsManager.LoadAll ran yet) and wraps them in a cookie
    /// account. Non-destructive: the legacy values are left in place.
    /// </summary>
    private static AccountRecord? TryMigrateLegacyYouTube()
    {
        string cookie = Preferences.Default.Get(LegacyCookieKey, "");
        if (string.IsNullOrWhiteSpace(cookie)) return null;

        string email = Preferences.Default.Get(LegacyAccountEmailKey, "");
        string name = Preferences.Default.Get(LegacyAccountNameKey, "");
        string handle = Preferences.Default.Get(LegacyChannelHandleKey, "");

        var data = new YouTubeAuthData
        {
            Cookie = cookie,
            VisitorData = Preferences.Default.Get(LegacyVisitorDataKey, ""),
            DataSyncId = Preferences.Default.Get(LegacyDataSyncIdKey, ""),
            PoToken = Preferences.Default.Get(LegacyPoTokenKey, ""),
            AccountName = name,
            AccountEmail = email,
            ChannelHandle = handle
        };

        return new AccountRecord
        {
            Platform = MusicPlatform.YouTube,
            YouTubeKind = YouTubeAuthKind.Cookies,
            DisplayName = !string.IsNullOrWhiteSpace(email) ? email
                        : !string.IsNullOrWhiteSpace(handle) ? handle
                        : !string.IsNullOrWhiteSpace(name) ? name
                        : "YouTube (imported cookies)",
            AuthDataJson = JsonSerializer.Serialize(data)
        };
    }
}
