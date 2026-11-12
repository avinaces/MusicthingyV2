using System.Text.Json.Serialization;

namespace Musicthingy.Services.Accounts;

/// <summary>
/// Platforms that support signed-in accounts inside Musicthingy.
/// </summary>
public enum MusicPlatform
{
    YouTube = 0,
    Spotify = 1
}

/// <summary>
/// How a YouTube account authenticates against InnerTube.
/// </summary>
public enum YouTubeAuthKind
{
    /// <summary>OAuth device-flow tokens (no cookie paste; auto-refreshing).</summary>
    OAuth = 0,

    /// <summary>Raw browser cookies (SAPISID &amp; friends) — the legacy path.</summary>
    Cookies = 1
}

/// <summary>
/// One signed-in account. AuthData holds the platform-specific JSON payload
/// (see <see cref="YouTubeAuthData"/> / <see cref="SpotifyAuthData"/>).
/// </summary>
public class AccountRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public MusicPlatform Platform { get; set; }

    /// <summary>Human label shown in the UI ("user@gmail.com", "spotify:user", ...).</summary>
    public string DisplayName { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Which auth payload this account uses (only meaningful for YouTube).</summary>
    public YouTubeAuthKind YouTubeKind { get; set; } = YouTubeAuthKind.OAuth;

    /// <summary>Platform-specific auth payload, serialized as JSON.</summary>
    public string AuthDataJson { get; set; } = "{}";

    public T? DeserializeAuth<T>()
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(AuthDataJson);
        }
        catch
        {
            return default;
        }
    }

    public void SerializeAuth<T>(T data)
    {
        AuthDataJson = System.Text.Json.JsonSerializer.Serialize(data);
    }
}

/// <summary>
/// YouTube auth payload. Exactly one of the two modes is populated,
/// selected by <see cref="AccountRecord.YouTubeKind"/>.
/// </summary>
public class YouTubeAuthData
{
    // ── OAuth mode ────────────────────────────────────────────────────────
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    /// <summary>Unix seconds when the access token expires.</summary>
    public long AccessTokenExpiresAtUnix { get; set; }
    public string? Scope { get; set; }

    // ── Cookie mode (legacy / OpenTune import) ────────────────────────────
    public string? Cookie { get; set; }
    public string? VisitorData { get; set; }
    public string? DataSyncId { get; set; }
    public string? PoToken { get; set; }
    public string? AccountName { get; set; }
    public string? AccountEmail { get; set; }
    public string? ChannelHandle { get; set; }

    [JsonIgnore]
    public bool HasOAuth => !string.IsNullOrEmpty(RefreshToken) && !string.IsNullOrEmpty(AccessToken);

    [JsonIgnore]
    public bool HasCookies => !string.IsNullOrEmpty(Cookie);
}

/// <summary>
/// Spotify auth payload. The actual Spotify credentials live inside the
/// go-librespot daemon's per-account config dir (state.json — the daemon
/// stores its own login blob); this record only points at it and remembers
/// the local API port assigned to that account.
/// </summary>
public class SpotifyAuthData
{
    /// <summary>The Spotify username the daemon reported after login.</summary>
    public string? Username { get; set; }

    /// <summary>Local port the daemon's REST/WS API binds for this account.</summary>
    public int ApiPort { get; set; }

    /// <summary>Name of the per-account config directory under AppData/spotify.</summary>
    public string ConfigDirName { get; set; } = "";
}

/// <summary>
/// Startup gateway used by the legacy hidden settings. Before the
/// AccountManager initializes, the hidden settings push directly to the
/// resolver (old behaviour). After initialization the AccountManager is the
/// single source of truth and legacy pushes are ignored — this prevents
/// SettingsManager.LoadAll() from overwriting the active account's state.
/// </summary>
public static class AccountsAuthBridge
{
    /// <summary>Set to true by AccountManager.InitializeAsync() once it has pushed state.</summary>
    public static volatile bool AccountManagerOwnsYouTube;

    /// <summary>Static gateway for the legacy cookie setting push.</summary>
    public static void PushCookie(string? value)
    {
        if (AccountManagerOwnsYouTube) return;
        YouTubeResolverService.Cookies = value;
    }

    public static void PushVisitorData(string? value)
    {
        if (AccountManagerOwnsYouTube) return;
        YouTubeResolverService.VisitorData = value;
    }

    public static void PushPoToken(string? value)
    {
        if (AccountManagerOwnsYouTube) return;
        YouTubeResolverService.PoToken = value;
    }

    public static void PushDataSyncId(string? value)
    {
        if (AccountManagerOwnsYouTube) return;
        YouTubeResolverService.DataSyncId = value;
    }
}
