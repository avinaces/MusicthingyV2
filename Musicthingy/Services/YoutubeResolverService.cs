using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services.PoToken;
using Musicthingy.Services.Sabr;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.Services;

// ────────────────────────────────────────────────────────────────────────────
// YoutubeResolverService — YouTube stream resolution with multi-client fallback.
//
// Client chain (v8.1, Nov 2026):
//   anonymous: VISIONOS → TVHTML5 (Samsung TV) → WEB (anon)
//   authed:    VISIONOS → TVHTML5 → WEB_EMBEDDED_PLAYER → WEB (authed) → WEB (anon)
//   yt-dlp's own production defaults, for reference (2026.10.26):
//     anonymous: tv_samsung → visionos → web
//     authed:    web_embedded → tv_samsung → web
//     premium:   web_creator → tv_samsung → web
//   Last: YoutubeExplode (with cookies if available) — cipher deciphering fallback.
//
// v8.2 (Nov 2026): every WEB-family / TVHTML5 /player payload now carries
// playbackContext.contentPlaybackContext.signatureTimestamp — the sts of
// the LIVE base.js player script. Since YouTube's March 2026 player updates
// (yt-dlp #16212, YouTube.js, FreeTube) a missing/stale sts makes those
// clients answer UNPLAYABLE "The page needs to be reloaded." even
// anonymously — exactly what killed TVHTML5 + WEB(anon) in the v8.1 field
// logs. The sts is a plain literal fetched from /iframe_api → base.js
// (no JS engine needed), cached 6h, self-invalidated on that exact error.
//
// ANDROID was REMOVED in v8.1 (user call + the other AI's field-log analysis):
// it answers SABR-only, and GVS demands a DroidGuard attestation token for it
// — bound to the official app's package name + signing key and minted through
// Play Services, so no third-party app can obtain one (the whole POT
// ecosystem — bgutils, yt-dlp, NewPipe — only mints Web-family WebPO tokens).
// The built-in generator mints exactly those, so the SABR fallback must come
// from WEB / WEB_EMBEDDED_PLAYER; the anonymous WEB entry provides exactly
// that and also dodges the cookie "The page needs to be reloaded." rejection
// that killed authed WEB in the field logs.
//
// ANDROID_VR was deliberately NOT added: yt-dlp's client notes (Oct 2026) say
// clientVersion 1.65.10 gets ALL formats 403'd since 2026.08.17, and versions
// above 1.65 return SABR-only — the XR-family exemption ended. VISIONOS is
// kept (it still works for ~90% of videos).
//
// Every resolve records a ResolveAttempt — the per-client outcomes and flags
// (SABR-only, login required, PO token attached). The playback error dialog
// reads it via GetLastResolveAttempt(), so the user sees the REAL per-client
// reasons from the resolve log instead of a guessed message.
//
// SABR: YouTube's new server-side streaming protocol (proprietary binary,
// replaces DASH/progressive URLs). When a video only returns SABR formats
// (no direct URLs, no signatureCipher), the resolver captures what a SABR
// streaming session needs (serverAbrStreamingUrl + ustreamer config + the
// audio formats) and — if every client in the chain came up empty — starts a
// SABR session itself (Services/Sabr/, a clean-room port of yt-dlp's SABR
// downloader flow) and returns an opaque "sabr://<guid>" URI. The Android
// playback service serves that URI through SabrDataSource (a Media3
// DataSource over the session's in-memory UMP stream). A GVS PO Token is
// attached to the SABR requests (v8.1 precedence): the BUILT-IN on-device
// generator first (Android: a hidden WebView running the youtube.com web
// player's own BotGuard attestation flow — Services/PoToken/ +
// Platforms/Android/WebViewPoTokenProvider.cs — WebPO tokens, content-bound
// to the video ID, accepted on Web-family sessions only), then a manually
// imported ***PO TOKEN*** pair, then an optional bgutil provider server.
// See Docs/SABR_Research.md for the protocol details.
// ────────────────────────────────────────────────────────────────────────────

public partial class YouTubeResolverService
{
    // ── Constants ───────────────────────────────────────────────────────────
    private const double CacheExpiryHours = 2.0;
    private const int ManifestFetchTimeoutSeconds = 60;
    private const int VideoInfoFetchTimeoutSeconds = 10;
    private const int MaxClientAttempts = 2;

    // How many cipher formats one client may probe before it is considered
    // blocked. TVHTML5's deciphered URLs fail per-stream (one itag 403s while
    // the next plays), so a single failed probe must not condemn the client.
    // 6 covers every audio format a client returns in practice (Opus 251/250/
    // 249 + AAC 140/139): stopping at 3 used to give up before the M4A
    // formats — the ones that most often survive when the WebM/Opus URLs 403.
    private const int MaxCipheredFormatAttempts = 6;

    // v9.5: how many times a deciphered googlevideo URL may be re-probed after
    // an HTTP 403 before its format is considered dead. Measured field pattern
    // (TVHTML5, several runs): the first GETs of a freshly deciphered URL are
    // rejected and a later GET of the SAME URL is served — 403, 403, 206 for
    // itag 251, sometimes 403, 403, 403, 206. Treating the first 403 as fatal
    // used to demote every playback to itag 250/249/140 (and made MWEB look
    // like it had no usable format at all).
    private const int CipheredProbeRetries = 3;
    private const int CipheredProbeRetryDelayMs = 400;
    private const int MaxCacheSize = 500;
    private const int CacheEvictBatch = 50;

    /// <summary>
    /// YouTube's public Android-client API key. Not a secret — it's embedded in
    /// the YouTube APK and documented in every open-source YouTube scraper.
    /// Static property so it can be overridden if YouTube rotates it.
    /// </summary>
    public static string InnerTubeApiKey { get; set; } = "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w";

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>
    /// v8.5: the CLEAN mobile-Chrome UA (yt-dlp's mweb client UA) — single
    /// source of truth shared by InnerTubeClients.Mweb AND the BotGuard
    /// WebView. The WebView must present a real-browser UA: its native UA
    /// carries the embedded-WebView markers "; wv)" + "Version/4.0", and
    /// under those GenerateIT returns 200 with NO integrity token
    /// ("GenerateIT returned no integrity token") — Google refuses to
    /// attest embedded WebViews. A desktop-Chrome UA also passes GenerateIT
    /// (proven in the field), but the mobile one matches the Android device
    /// reality (navigator.platform = Linux aarch64, Client Hints = Android)
    /// AND makes the mint environment 1:1 identical to the MWEB /player +
    /// SABR client that consumes the token.
    /// </summary>
    public const string MwebUserAgent =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

    // ── Shared HTTP handler (OS default network stack) ──────────────────────
    // v8.3: NO forced-IPv4 ConnectCallback anymore. A BotGuard PO token is
    // cryptographically bound to the EGRESS IP that minted it — the WebView
    // (Chromium, Happy Eyeballs) connects to jnn-pa.googleapis.com over IPv6
    // whenever the network offers it, so forcing this handler onto IPv4 made
    // every /player + googlevideo request leave from a DIFFERENT address
    // than the one that fetched the token ⇒ GVS answered
    // StreamProtectionStatus=2 (attestation pending). Both stacks now share
    // the OS default (same Happy Eyeballs ordering as the WebView).
    private static readonly SocketsHttpHandler _ipv4Handler = new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public static SocketsHttpHandler Ipv4Handler => _ipv4Handler;

    // v9.3: cookie-free handler for the InnerTube client. SocketsHttpHandler
    // defaults to UseCookies=true, so every Set-Cookie YouTube answered
    // (iframe_api, base.js, WEB/MWEB/TVHTML5 /player responses …) piled up in
    // _ipv4Handler.CookieContainer and leaked into later "anonymous"
    // requests — most visibly TVHTML5 going 403 mid-session after a fresh
    // restart, because restart RESETS the container while a running app kept
    // replaying the accumulated cookies. Auth cookies are attached
    // per-request (Cookie/SAPISIDHASH headers in BuildPlayerRequestAsync),
    // so nothing legitimate loses its cookies by turning the container off.
    private static readonly SocketsHttpHandler _innerTubeHandler = new()
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    public static SocketsHttpHandler InnerTubeHandler => _innerTubeHandler;

    // ── HTTP clients ────────────────────────────────────────────────────────
    private readonly HttpClient _innerTubeClient;
    private readonly YoutubeClient _youtubeClient;
    private static YoutubeClient? _cookieClientCache;

    // ── Immutable auth state (thread-safe via Interlocked.Exchange) ──────────
    private static PlaybackAuthState _authState = PlaybackAuthState.Empty;

    private static PlaybackAuthState AuthState
    {
        get => Volatile.Read(ref _authState);
        set => Interlocked.Exchange(ref _authState, value);
    }

    public static string? Cookies
    {
        get => AuthState.Cookie;
        set
        {
            AuthState = AuthState with { Cookie = string.IsNullOrWhiteSpace(value) ? null : value };
            _cookieClientCache = null; // invalidate cache
        }
    }

    public static bool HasCookies => !string.IsNullOrWhiteSpace(AuthState.Cookie);

    /// <summary>
    /// OAuth token provider for the active YouTube account (device-flow
    /// login). Returns a fresh Bearer token, or null when the active account
    /// uses cookies / is anonymous. Set by the AccountManager.
    /// </summary>
    public static Func<Task<string?>>? OAuthTokenProvider { get; set; }

    /// <summary>True when either cookies or an OAuth token provider is active.</summary>
    public static bool HasAuthentication => HasCookies || OAuthTokenProvider != null;

    /// <summary>
    /// Fired when YouTube rotates a session cookie (Set-Cookie on an innertube
    /// response). The AccountManager persists the fresh value into the active
    /// cookie account, so sessions captured via web login survive YouTube's
    /// short-lived __Secure-*PSIDTS rotation window.
    /// </summary>
    public static event Action<string, string>? SessionCookieRefreshed;


    public static string? VisitorData
    {
        get => AuthState.VisitorData;
        set => AuthState = AuthState with { VisitorData = string.IsNullOrWhiteSpace(value) ? null : value };
    }

    /// <summary>dataSyncId for authenticated session continuity. Stored for future use.</summary>
    public static string? DataSyncId
    {
        get => AuthState.DataSyncId;
        set => AuthState = AuthState with { DataSyncId = string.IsNullOrWhiteSpace(value) ? null : value };
    }

    /// <summary>PO Token for bot-attestation bypass. Stored for future use.</summary>
    public static string? PoToken
    {
        get => AuthState.PoToken;
        set => AuthState = AuthState with { PoToken = string.IsNullOrWhiteSpace(value) ? null : value };
    }

    // ── Resolve attempt tracking ──────────────────────────────────────────
    // Every resolve pass records what each client ACTUALLY answered; the
    // playback error dialog shows it (GetLastResolveAttempt) instead of
    // guessing at causes. Also mirrored to logcat via Trace().

    /// <summary>
    /// One resolve pass for one video: the per-client outcomes plus the
    /// SABR / sign-in / PO-token flags. Populated by GetFastStreamUrlAsync
    /// and the YoutubeExplode fallback.
    /// </summary>
    public sealed class ResolveAttempt
    {
        public string VideoId { get; }
        public DateTime At { get; } = DateTime.Now;
        public bool Succeeded { get; internal set; }
        public bool SabrOnly { get; internal set; }        // ≥1 client: audio formats but no direct URLs
        public bool LoginRequired { get; internal set; }   // ≥1 client asked for sign-in
        public bool PoTokenAttached { get; internal set; } // ≥1 request carried a GVS PO token
        public string? YoutubeExplodeError { get; internal set; }

        private readonly List<string> _outcomes = new();
        public IReadOnlyList<string> Outcomes
        {
            get { lock (_outcomes) return _outcomes.ToArray(); }
        }
        internal void Outcome(string line)
        {
            lock (_outcomes) _outcomes.Add(line);
        }

        public ResolveAttempt(string videoId) => VideoId = videoId;
    }

    private static readonly ConcurrentDictionary<string, ResolveAttempt> _lastAttempts = new();
    private const int MaxTrackedAttempts = 32;

    /// <summary>
    /// The most recent resolve attempt for a video (max age 10 minutes), or
    /// null. Read by the playback error dialog to show what YouTube actually
    /// said on every client.
    /// </summary>
    public static ResolveAttempt? GetLastResolveAttempt(string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;
        if (_lastAttempts.TryGetValue(videoId, out ResolveAttempt? attempt) &&
            (DateTime.Now - attempt.At).TotalMinutes < 10)
            return attempt;
        return null;
    }

    private static void RecordResolveAttempt(ResolveAttempt attempt)
    {
        if (_lastAttempts.Count >= MaxTrackedAttempts)
        {
            foreach (string key in _lastAttempts
                .OrderBy(kv => kv.Value.At)
                .Take(Math.Max(1, MaxTrackedAttempts / 4))
                .Select(kv => kv.Key)
                .ToList())
            {
                _lastAttempts.TryRemove(key, out _);
            }
        }
        _lastAttempts[attempt.VideoId] = attempt;
    }

    /// <summary>
    /// [Resolve] tracing that survives release builds: Debug.WriteLine PLUS
    /// Android logcat (tag "Musicthingy"), like the other services in this
    /// package.
    /// </summary>
    private static void Trace(string message)
    {
        Debug.WriteLine(message);
#if ANDROID
        Android.Util.Log.Info("Musicthingy", message);
#endif
    }

    // ── Per-client circuit breaker (15 s cooldown on HTTP 403) ───────────────
    // v9.3: was 10 minutes — one edge 403 locked TVHTML5 out until the app
    // was restarted. 15 s rides out transient GVS refusals without a
    // session-long lockout.
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();
    private static readonly TimeSpan FailedClientBackoff = TimeSpan.FromSeconds(15);

    private static void MarkStreamClientFailed(string videoId, string clientKey, int httpStatus)
    {
        if (httpStatus != 403) return;
        _failedUntil[$"{videoId}:{clientKey}"] = DateTime.UtcNow + FailedClientBackoff;
        Trace($"[Resolve] Circuit breaker OPEN for {videoId}:{clientKey}");
    }

    private static bool IsStreamClientBlocked(string videoId, string clientKey)
    {
        string key = $"{videoId}:{clientKey}";
        if (!_failedUntil.TryGetValue(key, out DateTime until)) return false;
        if (until <= DateTime.UtcNow) { _failedUntil.TryRemove(key, out _); return false; }
        return true;
    }

    // ── Stream URL cache (LRU eviction) ──────────────────────────────────────
    private readonly ConcurrentDictionary<string, (ResolvedMedia Media, DateTime FetchedAt)> _streamUrlCache = new();

    private void CacheStreamUrl(string trackUrl, ResolvedMedia media)
    {
        if (_streamUrlCache.Count >= MaxCacheSize)
        {
            var oldest = _streamUrlCache
                .OrderByDescending(kv => kv.Value.FetchedAt)
                .Skip(MaxCacheSize - CacheEvictBatch)
                .Select(kv => kv.Key)
                .ToList();
            foreach (string key in oldest)
                _streamUrlCache.TryRemove(key, out _);
        }
        _streamUrlCache[trackUrl] = (media, DateTime.Now);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"\[?(?:(?:(\d{1,2}):)?(\d{1,2}):(\d{2}))\]?\s*(?:[-:]\s*)?(.+)")]
    private static partial Regex ChapterRegex();

    // ── Constructor ──────────────────────────────────────────────────────────
    public YouTubeResolverService(IHttpClientFactory? httpFactory = null)
    {
        if (httpFactory != null)
            _innerTubeClient = httpFactory.CreateClient("youtube");
        else
            _innerTubeClient = new HttpClient(_innerTubeHandler) { Timeout = TimeSpan.FromSeconds(30) };

        _youtubeClient = new YoutubeClient(new HttpClient(_ipv4Handler));
    }

    /// <summary>Exposes the shared anonymous YoutubeExplode client for external use (e.g. cache downloads).</summary>
    public YoutubeClient GetYoutubeExplodeClient() => _youtubeClient;

    /// <summary>
    /// Creates (or returns cached) YoutubeExplode client with cookies attached.
    /// Cached statically — invalidated when Cookies setter is called.
    /// </summary>
    public static YoutubeClient? GetYoutubeClientWithCookies()
    {
        if (!HasCookies) return null;
        if (_cookieClientCache != null) return _cookieClientCache;

        try
        {
            var cookies = ParseCookieString(Cookies!);
            if (cookies.Count == 0) return null;

            var http = new HttpClient(_ipv4Handler, disposeHandler: false);
            _cookieClientCache = new YoutubeClient(http, cookies);
            return _cookieClientCache;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Resolve] Failed to create YoutubeExplode with cookies: {ex.Message}");
            return null;
        }
    }

    private static List<Cookie> ParseCookieString(string cookieString)
    {
        var cookies = new List<Cookie>();
        foreach (string part in cookieString.Split(';', '&'))
        {
            string trimmed = part.Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            string name = trimmed[..eq].Trim();
            string value = trimmed[(eq + 1)..].Trim();

            if (!string.IsNullOrEmpty(name))
                cookies.Add(new Cookie(name, value) { Domain = ".youtube.com" });
        }
        return cookies;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VideoChapter>> GetVideoChaptersAsync(string trackUrl)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return Array.Empty<VideoChapter>();

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));
            Video video = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            string description = video.Description;

            if (string.IsNullOrWhiteSpace(description))
                return Array.Empty<VideoChapter>();

            MatchCollection matches = ChapterRegex().Matches(description);
            List<VideoChapter> validChapters = new();
            bool hasFoundStart = false;

            foreach (Match match in matches)
            {
                int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
                int minutes = int.Parse(match.Groups[2].Value);
                int seconds = int.Parse(match.Groups[3].Value);

                TimeSpan timestamp = new(hours, minutes, seconds);
                string title = match.Groups[4].Value.Trim();

                if (!hasFoundStart)
                {
                    if (timestamp == TimeSpan.Zero)
                    {
                        hasFoundStart = true;
                        validChapters.Add(new VideoChapter(timestamp, title));
                    }
                }
                else if (timestamp > validChapters[^1].Timestamp)
                {
                    validChapters.Add(new VideoChapter(timestamp, title));
                }
            }

            return validChapters.Count >= 3 ? validChapters : Array.Empty<VideoChapter>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Chapters] Error for {trackUrl}: {ex.Message}");
            return Array.Empty<VideoChapter>();
        }
    }

    public async Task<IReadOnlyList<YouTubePlaylistVideo>> GetPlaylistVideosAsync(
        string playlistUrl, CancellationToken cancellationToken = default)
    {
        PlaylistId? playlistId = PlaylistId.TryParse(playlistUrl);
        if (playlistId == null) return Array.Empty<YouTubePlaylistVideo>();

        try
        {
            List<YouTubePlaylistVideo> videos = new();

            await foreach (PlaylistVideo video in _youtubeClient.Playlists.GetVideosAsync(playlistId.Value, cancellationToken))
            {
                string thumb = MediaUrlHelper.GetYouTubeThumbnail(video.Url)
                               ?? $"https://img.youtube.com/vi/{video.Id}/mqdefault.jpg";

                videos.Add(new YouTubePlaylistVideo(
                    Title: video.Title,
                    Url: video.Url,
                    ThumbnailUrl: thumb,
                    Duration: video.Duration
                ));
            }

            return videos;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playlist] Error for {playlistUrl}: {ex.Message}");
            return Array.Empty<YouTubePlaylistVideo>();
        }
    }

    /// <summary>
    /// Resolves a track URL to a direct stream URL.
    /// Tries the InnerTube client chain, then YoutubeExplode as a fallback.
    /// </summary>
    public async Task<ResolvedMedia?> ResolveMediaSourceAsync(
        string trackUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(trackUrl))
            return null;

        if (!MediaUrlHelper.IsYouTubeUrl(trackUrl))
        {
            bool isLocalFile = trackUrl.StartsWith("/", StringComparison.Ordinal) ||
                               trackUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                               trackUrl.StartsWith("content://", StringComparison.OrdinalIgnoreCase);

            // A direct HTTP source (Bandcamp, file host, ...) has no manifest:
            // its URL extension is the only format hint available.
            (string? codec, string? container) = isLocalFile ? (null, null) : DescribeUrlFormat(trackUrl);

            return new ResolvedMedia(
                trackUrl, DefaultUserAgent,
                Source: isLocalFile ? "Local file" : "Direct URL",
                StreamType: isLocalFile ? "Local file" : "Direct HTTP",
                Codec: codec,
                Container: container);
        }

        // Cache check. Test-only bypass: a cached ResolvedMedia would answer a
        // per-client experiment with a URL that a DIFFERENT client resolved in
        // an earlier run (the process survives `am start`), so the test would
        // silently test nothing.
        if (!BypassUrlCacheForTest &&
            _streamUrlCache.TryGetValue(trackUrl, out (ResolvedMedia Media, DateTime FetchedAt) cached)
            && (DateTime.Now - cached.FetchedAt).TotalHours < CacheExpiryHours)
        {
            Debug.WriteLine("[Resolve] CACHE HIT");
            return cached.Media;
        }

        ResolvedMedia? resolvedMedia = null;
        VideoId? videoId = VideoId.TryParse(trackUrl);

        if (videoId != null)
        {
            var attempt = new ResolveAttempt(videoId.Value.Value);
            Trace($"[Resolve] HasCookies={HasCookies}, ManualPoToken={!string.IsNullOrEmpty(PoToken)}" +
                  $", BuiltInPoTokenGen={BuiltInPoTokenProvider.IsActive}" +
                  $", PotProvider={PoTokenProviderUrl ?? "(none)"}, trying InnerTube clients for {videoId.Value.Value}");
            (resolvedMedia, SabrSessionConfig? sabrConfig, bool sabrAllowAuth) =
                await GetFastStreamUrlAsync(videoId.Value.Value, attempt, cancellationToken);

            // v7 (SABR): when every client only offered the video through
            // SABR (no direct URLs anywhere), stream it ourselves through the
            // UMP protocol client instead of failing. Android only — the
            // SabrDataSource is an ExoPlayer/Media3 construct; on Windows
            // the resolve chain behaves exactly as before.
            if (resolvedMedia == null && sabrConfig != null && OperatingSystem.IsAndroid())
            {
                Trace("[Resolve] All clients SABR-only — trying SABR streaming playback");
                resolvedMedia = await TrySabrPlaybackAsync(sabrConfig, sabrAllowAuth, attempt, cancellationToken);
            }

            // YoutubeExplode fallback — always try, even if InnerTube failed.
            // YoutubeExplode has its own cipher deciphering + watch-page scraping.
            // Test-only: skipped while a client filter is active — otherwise a
            // failed TVHTML5 run could "succeed" through YoutubeExplode and
            // report the wrong client as working.
            if (resolvedMedia == null && (!string.IsNullOrWhiteSpace(ClientsForTest) || ForceSabrForTest))
            {
                Trace($"[Resolve] YoutubeExplode fallback skipped (test: " +
                      (string.IsNullOrWhiteSpace(ClientsForTest) ? "mode sabr" : $"clients={ClientsForTest}") + ")");
            }
            else if (resolvedMedia == null)
            {
                Trace("[Resolve] InnerTube chain failed — trying YoutubeExplode fallback");
                try
                {
                    YoutubeClient? yteClient = HasCookies
                        ? GetYoutubeClientWithCookies()
                        : _youtubeClient;

                    if (yteClient != null)
                    {
                        AudioOnlyStreamInfo? bestStream = await Task.Run(async () =>
                        {
                            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromSeconds(ManifestFetchTimeoutSeconds));

                            StreamManifest manifest = await yteClient.Videos.Streams.GetManifestAsync(videoId.Value, cts.Token);

                            return manifest.GetAudioOnlyStreams()
                                .OrderByDescending(CodecRank)
                                .ThenByDescending(s => s.Bitrate)
                                .FirstOrDefault();
                        }, cancellationToken);

                        if (!string.IsNullOrEmpty(bestStream?.Url))
                        {
                            resolvedMedia = new ResolvedMedia(
                                bestStream.Url, DefaultUserAgent,
                                Source: "YouTube",
                                StreamType: "YoutubeExplode",
                                Client: "YOUTUBEEXPLODE",
                                Codec: bestStream.AudioCodec,
                                Container: bestStream.Container.Name,
                                BitrateKbps: bestStream.Bitrate.KiloBitsPerSecond > 0
                                    ? (int)bestStream.Bitrate.KiloBitsPerSecond
                                    : null);
                            Trace("[Resolve] YoutubeExplode SUCCESS");
                        }
                        else
                        {
                            attempt.YoutubeExplodeError = "no playable audio stream in manifest";
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    attempt.YoutubeExplodeError = ex.Message;
                    Trace($"[Resolve] YoutubeExplode error: {ex.Message}");
                }
            }

            attempt.Succeeded = resolvedMedia != null;
            RecordResolveAttempt(attempt);
        }

        if (resolvedMedia == null)
        {
            Trace($"[Resolve] ALL CLIENTS FAILED for {trackUrl} — returning null");
            return null;
        }

        if (!BypassUrlCacheForTest &&
            resolvedMedia.Url != trackUrl &&
            !resolvedMedia.Url.StartsWith("sabr://", StringComparison.Ordinal))
        {
            // sabr:// URIs are deliberately NOT cached: the session registry
            // keeps and evicts sessions on its own, and a cached sabr:// URI
            // could outlive its session.
            CacheStreamUrl(trackUrl, resolvedMedia);
        }

        return resolvedMedia;
    }

    public async Task<TimeSpan?> GetVideoDurationAsync(string trackUrl, CancellationToken cancellationToken)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return null;

        // Try InnerTube player endpoint — try VISIONOS first, then WEB+auth if available.
        // This is important: duration lookup for restricted videos needs the same client
        // chain as stream resolution, otherwise duration fails silently.
        InnerTubeClientInfo[] durationClients = HasAuthentication
            ? new[] { InnerTubeClients.VisionOS, InnerTubeClients.TvSamsung, InnerTubeClients.Web }
            : new[] { InnerTubeClients.VisionOS, InnerTubeClients.TvSamsung };

        foreach (var client in durationClients)
        {
            try
            {
                // v8.2: the same WEB-family/TVHTML5 signatureTimestamp gate as
                // the resolve chain — without a current sts those clients
                // answer "The page needs to be reloaded." here too.
                int? sts = (client.Name.StartsWith("WEB") || client.Name == "TVHTML5")
                    ? await GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                HttpRequestMessage request = await BuildPlayerRequestAsync(videoId.Value.Value, client);
                request.Content = BuildPlayerPayload(videoId.Value.Value, client, true, sts);

                HttpResponseMessage response = await _innerTubeClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                using (response)
                {
                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("videoDetails", out JsonElement videoDetails) &&
                        videoDetails.TryGetProperty("lengthSeconds", out JsonElement lengthSeconds) &&
                        long.TryParse(lengthSeconds.GetString(), out long secs))
                    {
                        return TimeSpan.FromSeconds(secs);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Duration] {client.Name} error: {ex.Message}");
            }
        }

        // YoutubeExplode fallback
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));

            Video videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            return videoInfo.Duration;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Duration] YoutubeExplode error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Fetches a video's TITLE via the same client chain as
    /// GetVideoDurationAsync (InnerTube player videoDetails.title, then
    /// YoutubeExplode). Used by the playlist-import safety net when the
    /// browse page's shape changes leave rows as "Untitled".
    /// </summary>
    public async Task<string?> GetVideoTitleAsync(string trackUrl, CancellationToken cancellationToken)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return null;

        InnerTubeClientInfo[] titleClients = HasAuthentication
            ? new[] { InnerTubeClients.VisionOS, InnerTubeClients.TvSamsung, InnerTubeClients.Web }
            : new[] { InnerTubeClients.VisionOS, InnerTubeClients.TvSamsung };

        foreach (var client in titleClients)
        {
            try
            {
                int? sts = (client.Name.StartsWith("WEB") || client.Name == "TVHTML5")
                    ? await GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                HttpRequestMessage request = await BuildPlayerRequestAsync(videoId.Value.Value, client);
                request.Content = BuildPlayerPayload(videoId.Value.Value, client, true, sts);

                HttpResponseMessage response = await _innerTubeClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                using (response)
                {
                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("videoDetails", out JsonElement videoDetails) &&
                        videoDetails.TryGetProperty("title", out JsonElement title) &&
                        title.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(title.GetString()))
                    {
                        return title.GetString();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Title] {client.Name} error: {ex.Message}");
            }
        }

        // YoutubeExplode fallback
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));

            Video videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            return videoInfo.Title;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Title] YoutubeExplode error: {ex.Message}");
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: client fallback chain
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test-only (adb: <c>--es clients tv,sabr</c>): builds the resolve chain
    /// from ONLY the named clients. Aliases:
    /// <list type="bullet">
    /// <item><c>sabr</c> — anonymous WEB + MWEB, the two clients that return a
    /// serverAbrStreamingUrl with no direct URLs (the SABR transport).</item>
    /// <item><c>tv</c>/<c>tvhtml5</c>/<c>tv_samsung</c> — TVHTML5 (Tizen),
    /// authenticated when cookies/OAuth exist (pair with
    /// <c>identity anon</c> for the anonymous variant).</item>
    /// <item><c>web</c>, <c>mweb</c>, <c>web_embedded</c>, <c>visionos</c> —
    /// that single client; <c>web</c>/<c>web_embedded</c> are authenticated
    /// when auth exists.</item>
    /// </list>
    /// Several names may be comma-separated; order is kept. Unknown names are
    /// logged and ignored (an empty result fails the resolve loudly, which is
    /// the point of a test switch).
    /// </summary>
    private static List<(InnerTubeClientInfo Client, bool AllowAuth)> BuildTestClientChain(string filter)
    {
        List<(InnerTubeClientInfo Client, bool AllowAuth)> chain = new();

        void Add(InnerTubeClientInfo client, bool allowAuth)
        {
            if (!chain.Any(c => c.Client.Name == client.Name && c.AllowAuth == allowAuth))
                chain.Add((client, allowAuth));
        }

        foreach (string rawToken in filter.Split(
                     new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = rawToken.Trim().ToLowerInvariant();
            switch (token)
            {
                case "sabr":
                    Add(InnerTubeClients.Web, false);
                    Add(InnerTubeClients.Mweb, false);
                    break;
                case "web":
                    Add(InnerTubeClients.Web, HasAuthentication);
                    Add(InnerTubeClients.Web, false);
                    break;
                case "web_embedded":
                case "embedded":
                    Add(InnerTubeClients.WebEmbedded, HasAuthentication);
                    break;
                case "mweb":
                    Add(InnerTubeClients.Mweb, false);
                    break;
                case "tv":
                case "tvhtml5":
                case "tv_samsung":
                    // Both modes, exactly like the production chain: `identity
                    // anon` then drops the authenticated entry instead of
                    // leaving an empty chain.
                    Add(InnerTubeClients.TvSamsung, HasAuthentication);
                    Add(InnerTubeClients.TvSamsung, false);
                    break;
                case "visionos":
                    Add(InnerTubeClients.VisionOS, false);
                    break;
                default:
                    Trace($"[Resolve] clients={filter}: unknown client '{rawToken}' (test) ignored");
                    break;
            }
        }

        return chain;
    }

    /// <summary>
    /// True when the test client filter names the <c>sabr</c> alias, i.e. the
    /// run wants the SABR transport (not a direct URL that those clients may
    /// also return).
    /// </summary>
    private static bool ClientsForTestContainsSabr(string filter) =>
        filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Trim().Equals("sabr", StringComparison.OrdinalIgnoreCase));

    private async Task<(ResolvedMedia? Media, SabrSessionConfig? SabrConfig, bool SabrAllowAuth)> GetFastStreamUrlAsync(
        string videoId, ResolveAttempt attempt, CancellationToken cancellationToken,
        string? singleClientName = null, bool singleAllowAuth = true)
    {
        // Test-only: --es mode sabr (or the `sabr` client alias below) makes
        // every DIRECT URL unusable so the resolve must take the SABR path.
        bool skipDirectUrlsForTest = ForceSabrForTest;

        // Build the client chain (v8.1). The authenticated clients are only
        // included when cookies OR an OAuth Bearer are available.
        //
        // v8.1: ANDROID is GONE (see the file header) — the SABR fallback
        // must come from a WEB-family client so the built-in WebPO tokens
        // actually attach to something GVS accepts.
        List<(InnerTubeClientInfo Client, bool AllowAuth)> clientsToTry;
        if (singleClientName != null)
        {
            // Continuation fast path: a RUNNING SABR stream only ever needs
            // a fresh signed serverAbrStreamingUrl from the SAME client that
            // started it — a direct URL cannot be spliced into the running
            // stream, so the rest of the chain is pointless here. One
            // request (~1-2s) instead of the full chain (~20s observed in
            // logcat); the caller falls back to the full chain when it
            // yields nothing.
            InnerTubeClientInfo target = singleClientName switch
            {
                "WEB" => InnerTubeClients.Web,
                "MWEB" => InnerTubeClients.Mweb,
                "WEB_EMBEDDED_PLAYER" => InnerTubeClients.WebEmbedded,
                "TVHTML5" => InnerTubeClients.TvSamsung,
                "VISIONOS" => InnerTubeClients.VisionOS,
                _ => InnerTubeClients.Web,
            };
            clientsToTry = new() { (target, singleAllowAuth) };
            Trace($"[Resolve] Client chain: {target.Name}{(singleAllowAuth ? "" : " (anon)")} (continuation fast path)");
        }
        else
        {
            clientsToTry = new()
            {
                (InnerTubeClients.VisionOS, false),

                // tv_samsung (clientName TVHTML5, Samsung Tizen smart TV):
                // yt-dlp's #1 default client for EVERYONE as of Oct 2026 — no
                // PO token required, returns direct URLs, supports cookies.
                (InnerTubeClients.TvSamsung, HasAuthentication),
            };

            if (HasAuthentication)
            {
                // yt-dlp's authenticated defaults: web_embedded → tv_samsung → web.
                // v9.1: an ANONYMOUS tv_samsung retry right after the
                // authenticated one. yt-dlp pulls this track's full 217s from
                // tv_samsung anonymously on this very IP, while our
                // cookie-authenticated TVHTML5 request gets a deciphered URL
                // that 403s on some runs — YouTube's per-request toggle, so the
                // authenticated client must not be the only TV attempt.
                clientsToTry.Add((InnerTubeClients.TvSamsung, false));
                clientsToTry.Add((InnerTubeClients.WebEmbedded, true));
                clientsToTry.Add((InnerTubeClients.Web, true));
                Trace($"[Resolve] Authentication detected ({(HasCookies ? "cookies" : "oauth")}) — WEB_EMBEDDED_PLAYER + WEB added to fallback chain");
            }

            // Anonymous WEB (v8.1): the Web-family SABR fallback that the
            // built-in WebPO tokens attach to. v8.9: placed BEFORE MWEB so its
            // SABR capture is the one kept — MWEB's /player answer is captured
            // too, but its deciphered media URL 403s immediately, which used to
            // trip MWEB's circuit breaker and leave the SABR continuation fast
            // path pointing at a blocked client.
            clientsToTry.Add((InnerTubeClients.Web, false));

            // MWEB (v8.8): still in the chain (its /player answer is a useful
            // extra fallback), but never the SABR session owner.
            if (OperatingSystem.IsAndroid())
                clientsToTry.Add((InnerTubeClients.Mweb, false));

            Trace($"[Resolve] Client chain: {string.Join(" → ", clientsToTry.Select(c => c.AllowAuth ? c.Client.Name : c.Client.Name + "(anon)"))}");
        }

        // Test-only: --es clients tv,sabr. REPLACES the production fallback
        // chain with exactly the named clients, so one transport can be
        // exercised deterministically (TVHTML5's direct URLs vs the
        // anonymous WEB/MWEB SABR path) instead of whatever the full chain
        // happens to reach first. Empty = production chain untouched.
        if (!string.IsNullOrWhiteSpace(ClientsForTest) && singleClientName == null)
        {
            clientsToTry = BuildTestClientChain(ClientsForTest);
            if (ClientsForTestContainsSabr(ClientsForTest) && !ForceSabrForTest)
            {
                // The alias is literally named after the transport: selecting
                // `sabr` must not quietly play through one of those clients'
                // ciphered direct URLs instead. Local to this resolve — the
                // static switch keeps its adb-controlled value.
                skipDirectUrlsForTest = true;
                Trace("[Resolve] clients=sabr (test): direct URLs skipped — SABR transport only");
            }
            Trace($"[Resolve] clients={ClientsForTest} (test): " +
                  (clientsToTry.Count == 0
                      ? "no known client name matched — chain is empty"
                      : string.Join(" → ", clientsToTry.Select(c => c.AllowAuth ? c.Client.Name : c.Client.Name + "(anon)"))));
        }

        // Test-only: --es identity anon. The SABR POST carries no cookies, so a
        // serverAbrStreamingUrl issued to an AUTHENTICATED /player call makes the
        // streamer an unattested stranger to that session. Forcing an anonymous
        // chain (which still gets a video-bound PO token) tells us whether the
        // cookie identity is what keeps StreamProtectionStatus at 2.
        if (ForceAnonymousForTest)
        {
            int removed = clientsToTry.RemoveAll(c => c.AllowAuth);
            Trace($"[Resolve] identity=anon (test): {removed} authenticated client(s) removed from the chain");
        }

        // A test filter + identity switch can legitimately leave nothing (e.g.
        // clients=web_embedded with no anonymous variant). Say so plainly
        // instead of failing with an empty chain and no explanation.
        if (clientsToTry.Count == 0)
            Trace($"[Resolve] Client chain is EMPTY after the test filters (clients={ClientsForTest ?? "all"}, identity={(ForceAnonymousForTest ? "anon" : "auto")}) — resolve will fail");

        // v7 (SABR): filled when a client answers SABR-only; used as the
        // last resort if the whole chain finds no direct URL. v8.1: the
        // AUTH MODE of the client that produced it travels alongside — the
        // continuation refresh re-resolves with the same client + mode.
        SabrSessionConfig? sabrConfig = null;
        bool sabrAllowAuth = false;

        foreach ((InnerTubeClientInfo client, bool allowAuth) in clientsToTry)
        {
            // v8.3: the WebView already holds youtube.com's own VISITOR_DATA
            // (the exact value the BotGuard minter saw at init). WEB-family
            // /player requests travel with it, so the minted session-bound
            // token and the player session share one identity. VISIONOS and
            // TVHTML5 keep whatever VisitorData the session already had —
            // their responseContext blobs (e.g. a 544-byte VISIONOS blob full
            // of experiment flags) must NOT leak into WEB requests.
            if ((client.Name.StartsWith("WEB", StringComparison.Ordinal) || client.Name == "MWEB") &&
                !ProviderConfigured &&
                !string.IsNullOrEmpty(BuiltInPoTokenProvider.WebVisitorData))
            {
                VisitorData = BuiltInPoTokenProvider.WebVisitorData;
            }

            string circuitKey = allowAuth ? client.Name : $"{client.Name}:anon";
            if (IsStreamClientBlocked(videoId, circuitKey))
            {
                Trace($"[Resolve] Skipping {circuitKey} — circuit breaker open for {videoId}");
                attempt.Outcome($"{circuitKey}: skipped (recent HTTP 403)");
                continue;
            }

            for (int tryIndex = 1; tryIndex <= MaxClientAttempts; tryIndex++)
            {
                try
                {
                    // v8.2: WEB / WEB_EMBEDDED_PLAYER / TVHTML5 demand a
                    // CURRENT signatureTimestamp (the sts of YouTube's live
                    // base.js player) in the payload — without it they answer
                    // UNPLAYABLE "The page needs to be reloaded." even
                    // anonymously. Cached 6h, so the chain pays for it once.
                    int? sts = (client.Name.StartsWith("WEB") || client.Name == "MWEB" || client.Name == "TVHTML5")
                        ? await GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false)
                        : null;

                    // v8.9: mint the content-bound token BEFORE /player for
                    // anonymous MWEB / WEB — from WHATEVER source is live
                    // (provider > built-in mint > manual pair). Without it the
                    // server issues serverAbrStreamingUrl from an UNATTESTED
                    // /player call and GVS answers StreamProtectionStatus=2 for
                    // the whole session. It costs nothing extra: the token is
                    // cached and the SABR session reuses the same value.
                    string? playerPot = null;
                    if (HasPoTokenSource && !allowAuth &&
                        (client.Name == "MWEB" || client.Name == "WEB"))
                    {
                        playerPot = await GetGvsPoTokenAsync(videoId, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    HttpRequestMessage request = await BuildPlayerRequestAsync(videoId, client, allowAuth);
                    request.Content = BuildPlayerPayload(videoId, client, allowAuth, sts, playerPot);

                    if (!string.IsNullOrEmpty(playerPot))
                        attempt.PoTokenAttached = true;

                    HttpResponseMessage response = await _innerTubeClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    using (response)
                    {
                        if ((int)response.StatusCode == 403)
                            MarkStreamClientFailed(videoId, circuitKey, 403);

                        if (!response.IsSuccessStatusCode)
                        {
                            Trace($"[Resolve] {client.Name} returned HTTP {(int)response.StatusCode} for {videoId}");
                            attempt.Outcome($"{client.Name}: HTTP {(int)response.StatusCode}");
                            break;
                        }

                        string json = await response.Content.ReadAsStringAsync(cancellationToken);

                        // Keep rotating session cookies (__Secure-*PSIDTS) fresh so
                        // cookie sessions captured via web login do not expire.
                        HarvestSessionCookies(response);

                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        // Update visitorData from response (for non-cookie-authenticated requests).
                        // SKIPPED while a visitorData-bound token is live: a manually imported
                        // ***VISITOR DATA*** + ***PO TOKEN*** pair (or a provider-minted token)
                        // must keep traveling with the visitorData it was minted for — adopting
                        // the response's visitorData would unbind them and the attestation
                        // would never pass. v8.1: the built-in generator's WebPO tokens are
                        // CONTENT-bound (video ID) — they don't care, so visitorData keeps
                        // rotating while the built-in mint is the live source (a dormant
                        // manual pair no longer pins it either).
                        bool manualTokenLive = !string.IsNullOrEmpty(PoToken) && !BuiltInPoTokenProvider.IsActive;
                        // v8.3: adopt the response's visitorData from WEB-family
                        // clients ONLY (WEB / WEB_EMBEDDED_PLAYER), and only while
                        // the WebView hasn't already supplied the authoritative
                        // youtube.com VISITOR_DATA. VISIONOS/TVHTML5 responses
                        // carry foreign blobs that poisoned WEB + BotGuard in
                        // the field (the 836-char token regression).
                        if (!manualTokenLive && _providerTokenCache.IsEmpty &&
                            (client.Name.StartsWith("WEB", StringComparison.Ordinal) || client.Name == "MWEB") &&
                            string.IsNullOrEmpty(BuiltInPoTokenProvider.WebVisitorData) &&
                            root.TryGetProperty("responseContext", out JsonElement rc) &&
                            rc.TryGetProperty("visitorData", out JsonElement vd))
                        {
                            string? newVd = vd.GetString();
                            if (!string.IsNullOrEmpty(newVd) && newVd != VisitorData)
                            {
                                VisitorData = newVd;
                                Trace("[Resolve] Updated visitorData from player response");
                            }
                        }

                        // Check playabilityStatus
                        if (root.TryGetProperty("playabilityStatus", out JsonElement statusProp) &&
                            statusProp.TryGetProperty("status", out JsonElement status))
                        {
                            string? statusStr = status.GetString();
                            if (statusStr != "OK")
                            {
                                string? reason = statusProp.TryGetProperty("reason", out JsonElement reasonEl)
                                    ? reasonEl.GetString() : null;
                                Trace($"[Resolve] {client.Name} returned {statusStr} for {videoId}: {reason ?? "no reason"}");
                                attempt.Outcome($"{client.Name}: {statusStr} — {reason ?? "no reason given"}");
                                if (statusStr == "LOGIN_REQUIRED")
                                    attempt.LoginRequired = true;

                                // v8.2: "The page needs to be reloaded." means a
                                // missing/stale signatureTimestamp — drop the cache
                                // so the next client in the chain re-fetches the
                                // live base.js sts instead of reusing the stale one.
                                if (reason?.Contains("reloaded", StringComparison.OrdinalIgnoreCase) == true)
                                    _cachedSts = null;
                                break; // try next client
                            }
                        }

                        // Extract best audio stream
                        if (root.TryGetProperty("streamingData", out JsonElement streamingData) &&
                            streamingData.TryGetProperty("adaptiveFormats", out JsonElement adaptiveFormats))
                        {
                            // Enumerate ONCE, collect stats
                            var formats = adaptiveFormats.EnumerateArray().ToList();
                            int audioCount = 0, withUrl = 0, withCipher = 0;

                            foreach (var f in formats)
                            {
                                bool isAudio = f.TryGetProperty("mimeType", out JsonElement m) &&
                                               m.GetString()!.Contains("audio/");
                                if (!isAudio) continue;

                                audioCount++;
                                if (f.TryGetProperty("url", out _)) withUrl++;
                                if (f.TryGetProperty("signatureCipher", out _)) withCipher++;
                            }

                            Trace($"[Resolve] {client.Name}: {formats.Count} formats, {audioCount} audio, {withUrl} direct, {withCipher} ciphered");

                            // v8.1 SABR capture: whenever the client offers no direct
                            // URLs, capture what a SABR session needs — even when
                            // signatureCipher formats are ALSO present (the WEB family
                            // returns that combination routinely; deciphering is now
                            // only the fallback). Web-family SABR sessions are what
                            // the built-in WebPO tokens attach to.
                            if (audioCount > 0 && withUrl == 0 &&
                                streamingData.TryGetProperty("serverAbrStreamingUrl", out JsonElement sabrUrlEl) &&
                                sabrUrlEl.GetString() is string sabrUrl && !string.IsNullOrEmpty(sabrUrl))
                            {
                                string cipherNote = withCipher > 0 ? $" (+{withCipher} ciphered also present)" : "";
                                attempt.SabrOnly = true;
                                Trace($"[Resolve] {client.Name}: SABR session captured — {audioCount} audio formats, no direct URLs{cipherNote}");
                                attempt.Outcome($"{client.Name}: SABR session available — {audioCount} audio formats, no direct URLs{cipherNote}");

                                // v8.6: keep the FIRST valid capture — a later
                                // SABR-only client (e.g. WEB anon after the
                                // authed WEB) must not overwrite it; the
                                // continuation fast path always takes the
                                // fresh capture.
                                // v8.7: EXCEPTION — the built-in mint's PO
                                // tokens are ANONYMOUS (WebView identity), so
                                // when it is active an anon WEB capture must
                                // REPLACE an authenticated one: 19:24 showed
                                // the anonymous WebView token posted over the
                                // AUTHED serverAbrStreamingUrl — GVS answered
                                // sps=2 on request #1.
                                if (sabrConfig is null || singleClientName != null ||
                                    (sabrAllowAuth && !allowAuth && BuiltInPoTokenProvider.IsActive))
                                {
                                    SabrSessionConfig? captured = TryBuildSabrConfig(root, formats, client, videoId, sabrUrl);
                                    if (captured != null)
                                    {
                                        sabrConfig = captured;
                                        sabrAllowAuth = allowAuth;
                                        // In the continuation fast path a fresh session URL
                                        // is ALL the caller needs — stop instead of burning
                                        // retries.
                                        if (singleClientName != null)
                                            return (null, sabrConfig, sabrAllowAuth);
                                    }
                                }
                            }
                            else if (audioCount > 0 && withUrl == 0 && withCipher == 0)
                            {
                                // SABR-only response WITHOUT a session URL — nothing
                                // this client can give us.
                                Trace($"[Resolve] {client.Name}: audio formats but no direct URLs and no serverAbrStreamingUrl");
                                attempt.Outcome($"{client.Name}: audio formats but no direct URLs and no SABR session URL");
                            }

                            if (withUrl > 0 && skipDirectUrlsForTest)
                            {
                                Trace($"[Resolve] {client.Name}: direct URLs skipped (test: force SABR)");
                                attempt.Outcome($"{client.Name}: {withUrl} direct URL(s) skipped (force SABR)");
                            }
                            else if (withUrl > 0)
                            {
                                // Direct URL — find best audio with a URL
                                AudioChoice? choice = FindBestAudioWithUrl(formats);
                                if (choice is { IsUltraLow: true } && sabrConfig != null)
                                {
                                    // v9.4: this client's only direct-URL audio is the
                                    // AUDIO_QUALITY_ULTRALOW saver tier (~31 kbps), and the
                                    // same response carries a SABR session. Keep the SABR
                                    // config and move on — another client, or the SABR path
                                    // at the end of the resolve, gives better audio.
                                    Trace($"[Resolve] {client.Name}: skippable ultra-low direct audio (itag {choice.Itag}) — SABR session available");
                                    attempt.Outcome($"{client.Name}: ultra-low direct audio skipped (SABR available)");
                                }
                                else if (choice != null)
                                {
                                    string streamUrl = choice.Url;
                                    string ua = EffectiveUserAgent(client);
                                    // EJS deciphering is independent of PO-token minting:
                                    // TVHTML5 needs sig/n solved even when the built-in
                                    // generator setting is OFF (it never uses PO tokens).
                                    if (BuiltInPoTokenProvider.HasChallengeSolver && GetQueryParamValue(streamUrl, "n") != null)
                                        streamUrl = await DescrambleSabrNParamAsync(streamUrl, cancellationToken).ConfigureAwait(false);

                                    int probeStatus = await ProbeStreamUrlAsync(streamUrl, ua, cancellationToken).ConfigureAwait(false);
                                    if (probeStatus >= 200 && probeStatus < 300)
                                    {
                                        Trace($"[Resolve] SUCCESS — client: {client.Name}{(allowAuth ? "" : " (anon)")} (direct URL)");
                                        attempt.Outcome($"{client.Name}: OK — direct audio URL");
                                        return (choice.ToResolvedMedia(streamUrl, ua, "Direct HTTP", client.Name), null, false);
                                    }

                                    Trace($"[Resolve] {client.Name}: direct URL returned HTTP {probeStatus} — trying next client");
                                    attempt.Outcome($"{client.Name}: direct URL HTTP {probeStatus}");
                                    MarkStreamClientFailed(videoId, circuitKey, probeStatus);
                                    break;
                                }
                                else
                                {
                                    Trace($"[Resolve] {client.Name}: direct URLs present but no usable audio format");
                                    attempt.Outcome($"{client.Name}: {withUrl} direct URL(s), none of them audio");
                                }
                            }

                            // EJS deciphering does NOT depend on the PO-token setting:
                            // TVHTML5 serves signatureCipher formats and needs sig/n
                            // solved even with the built-in generator switched off.
                            if (withCipher > 0 && singleClientName == null &&
                                BuiltInPoTokenProvider.HasChallengeSolver && skipDirectUrlsForTest)
                            {
                                Trace($"[Resolve] {client.Name}: ciphered URLs skipped (test: SABR transport only)");
                                attempt.Outcome($"{client.Name}: {withCipher} ciphered URL(s) skipped (test: SABR only)");
                            }
                            else if (withCipher > 0 && singleClientName == null && BuiltInPoTokenProvider.HasChallengeSolver)
                            {
                                // v9.0: MWEB serves ordinary HTTPS audio formats
                                // behind signatureCipher, and googlevideo requires
                                // the GVS token ON THE URL for that client
                                // (&pot=<token>) — without it the deciphered URL
                                // is always HTTP 403.
                                // v9.1: the &pot= token is the SAME content-bound
                                // (videoId) token that went into /player. yt-dlp's
                                // own note for this video says "bind GVS PO Token
                                // to video ID for web client", so the visitor-bound
                                // variant is only an opt-in experiment
                                // (UseVisitorBoundTokens, adb: --es binding visitor).
                                // TVHTML5 never wants &pot=, so it stays null.
                                string? cipherPot = null;
                                if (client.Name == "MWEB")
                                {
                                    cipherPot = playerPot;
                                    if (ProviderConfigured && UseVisitorBoundTokens &&
                                        !string.IsNullOrEmpty(VisitorData))
                                    {
                                        string? gvsPot = await GetGvsPoTokenAsync(VisitorData, cancellationToken)
                                            .ConfigureAwait(false);
                                        if (!string.IsNullOrEmpty(gvsPot)) cipherPot = gvsPot;
                                    }
                                }
                                string ua = EffectiveUserAgent(client);
                                HashSet<int> triedItags = new();
                                AudioChoice? lastTried = null;
                                int probeStatus = 0;
                                for (int formatTry = 0; formatTry < MaxCipheredFormatAttempts; formatTry++)
                                {
                                    AudioChoice? choice = await FindBestAudioWithCipherAsync(
                                            formats, cancellationToken, cipherPot, triedItags)
                                        .ConfigureAwait(false);
                                    if (choice == null) break;

                                    lastTried = choice;
                                    probeStatus = await ProbeStreamUrlAsync(choice.Url, ua, cancellationToken)
                                        .ConfigureAwait(false);

                                    // v9.5: the first GETs of a freshly deciphered URL are
                                    // rejected with 403 while a later request for the SAME
                                    // URL is served; the 403 is a per-request warm-up
                                    // rejection, not a bad signature. Re-probe before
                                    // condemning the format (field: 403, 403, 206 for itag
                                    // 251 — every run demoted to 250/249/140 before this).
                                    for (int retryTry = 1; probeStatus == 403 && retryTry <= CipheredProbeRetries; retryTry++)
                                    {
                                        await Task.Delay(CipheredProbeRetryDelayMs, cancellationToken).ConfigureAwait(false);
                                        int retryStatus = await ProbeStreamUrlAsync(choice.Url, ua, cancellationToken)
                                            .ConfigureAwait(false);
                                        Trace($"[Resolve] {client.Name}: itag {choice.Itag} HTTP 403, retry {retryTry}/{CipheredProbeRetries} -> {retryStatus}");
                                        probeStatus = retryStatus;
                                    }
                                    if (probeStatus >= 200 && probeStatus < 300)
                                    {
                                        Trace($"[Resolve] SUCCESS — client: {client.Name}{(allowAuth ? "" : " (anon)")} (EJS deciphered URL, itag {choice.Itag})");
                                        attempt.Outcome($"{client.Name}: OK — deciphered audio URL (itag {choice.Itag})");
                                        return (choice.ToResolvedMedia(choice.Url, ua, "EJS deciphered", client.Name), null, false);
                                    }

                                    // One 403 does not condemn the client: ciphered
                                    // URLs are per-stream, so try the next-best audio
                                    // format (itag) before moving on.
                                    triedItags.Add(choice.Itag);
                                    Trace($"[Resolve] {client.Name}: deciphered itag {choice.Itag} returned HTTP {probeStatus}" +
                                          (formatTry + 1 < MaxCipheredFormatAttempts
                                              ? " — trying next audio format"
                                              : " — trying next client"));
                                }

                                if (lastTried != null)
                                {
                                    attempt.Outcome($"{client.Name}: deciphered URL HTTP {probeStatus}");
                                    MarkStreamClientFailed(videoId, circuitKey, probeStatus);
                                    break;
                                }
                            }

                            if (withCipher > 0 && !skipDirectUrlsForTest)
                            {
                                Trace($"[Resolve] {client.Name}: ciphered URLs only — will use YoutubeExplode fallback");
                                attempt.Outcome($"{client.Name}: {withCipher} ciphered URL(s) — YoutubeExplode can decipher");
                            }
                        }
                        else
                        {
                            Trace($"[Resolve] {client.Name}: no streamingData in response");
                            attempt.Outcome($"{client.Name}: no streamingData in response");
                        }

                        break; // no usable streaming data, try next client
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (IOException ex)
                {
                    Trace($"[Resolve] {client.Name} try {tryIndex} IOException: {ex.Message}");
                    if (tryIndex < MaxClientAttempts)
                        await Task.Delay(500 * tryIndex, cancellationToken).ConfigureAwait(false);
                    else
                        attempt.Outcome($"{client.Name}: network error — {ex.Message}");
                }
                catch (Exception ex)
                {
                    Trace($"[Resolve] {client.Name} try {tryIndex} error: {ex.Message}");
                    attempt.Outcome($"{client.Name}: {ex.GetType().Name} — {ex.Message}");
                    break;
                }
            }
        }

        return (null, sabrConfig, sabrAllowAuth);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: SABR playback fallback (v7)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a SABR streaming session for a SABR-only video and pre-flights
    /// it (waits for the first bytes — the init segment — to arrive). On
    /// success the resolver returns an opaque "sabr://&lt;guid&gt;" URI which
    /// the Android playback service serves through SabrDataSource. On any
    /// failure the outcome is recorded in the attempt and null is returned,
    /// so the resolve chain continues with the YoutubeExplode fallback.
    /// </summary>
    private async Task<ResolvedMedia?> TrySabrPlaybackAsync(
        SabrSessionConfig config, bool sabrAllowAuth, ResolveAttempt attempt, CancellationToken cancellationToken)
    {
        SabrStreamSession? session = null;
        try
        {
            if (config.ClientInfo.ClientName is 1 or 2 or 56)
            {
                string descrambledUrl = await DescrambleSabrNParamAsync(config.ServerAbrStreamingUrl, cancellationToken).ConfigureAwait(false);
                if (descrambledUrl != config.ServerAbrStreamingUrl)
                    config = config with { ServerAbrStreamingUrl = descrambledUrl };

                // v9.1: BOTH tokens default to the content-bound (videoId)
                // mint, which is what yt-dlp resolves for this video
                // ("Detected experiment to bind GVS PO Token to video ID for
                // web client"): StreamerContext.po_token gets the same value as
                // /player and &pot=, SessionPoToken stays null. The
                // visitor-bound variant (the GVS/session mint bgutil's own
                // plugin uses for other videos) is an opt-in experiment —
                // v8.5 with the WebView mint and v9.0 with bgutil both failed
                // with it, so it only runs when explicitly requested.
                string? contentPoToken = await GetGvsPoTokenAsync(config.VideoId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(config.PoToken) && !string.IsNullOrEmpty(contentPoToken))
                {
                    config = config with { PoToken = contentPoToken };
                    attempt.PoTokenAttached = true;
                }

                if (ProviderConfigured && UseVisitorBoundTokens)
                {
                    string? gvsBinding = !string.IsNullOrEmpty(config.ClientInfo.VisitorData)
                        ? config.ClientInfo.VisitorData
                        : VisitorData;
                    if (!string.IsNullOrEmpty(gvsBinding))
                    {
                        string? sessionPoToken = await GetGvsPoTokenAsync(gvsBinding, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(sessionPoToken))
                        {
                            config = config with { SessionPoToken = sessionPoToken };
                            Trace($"[Resolve] SABR tokens: content-bound {config.PoToken?.Length ?? 0} chars " +
                                  $"(/player), visitor-bound {sessionPoToken.Length} chars (StreamerContext)");
                        }
                    }
                }
                else
                {
                    Trace($"[Resolve] SABR tokens: content-bound {config.PoToken?.Length ?? 0} chars " +
                          "(/player, &pot=, StreamerContext)");
                }
            }

            string? refreshClientName = SabrRefreshClientName(config.ClientInfo.ClientName);
            config = config with
            {
                Refresher = (fromTimeMs, ct) => RefreshSabrConfigAsync(
                    config.VideoId, refreshClientName, sabrAllowAuth, fromTimeMs, ct),
            };

            // Test-only cross-over: hand the fresh /player answer to an
            // external streamer instead of consuming it here.
            if (Sabr.SabrStreamSession.SkipSessionForTest)
            {
                Sabr.SabrStreamSession.LogConfig(config.ServerAbrStreamingUrl, config);
                Trace("[Resolve] SABR session skipped (test cross-over) — config dumped, URL left unused");
                attempt.Outcome("SABR: session skipped (test cross-over)");
                return null;
            }

            session = SabrStreamRegistry.GetOrCreate(config);
            session.EnsureRunning();

            using CancellationTokenSource preflightCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preflightCts.CancelAfter(TimeSpan.FromSeconds(12));

            long firstBytes = await session.WaitForFirstBytesAsync(1, preflightCts.Token);

            Trace($"[Resolve] SABR session started — itag {session.ActiveItag}, {firstBytes} bytes received");
            attempt.Outcome($"SABR: streaming session started (itag {session.ActiveItag}, {firstBytes} bytes received)");

            SabrAudioFormat? activeFormat = config.PreferredAudio
                .FirstOrDefault(f => f.Itag == session.ActiveItag)
                ?? config.PreferredAudio.FirstOrDefault();
            (string? codec, string? container) = SplitMimeType(activeFormat?.MimeType);

            return new ResolvedMedia(
                SabrStreamRegistry.MakeUri(session), DefaultUserAgent,
                Source: "YouTube",
                StreamType: "SABR",
                Client: SabrRefreshClientName(config.ClientInfo.ClientName)
                        ?? $"client {config.ClientInfo.ClientName}",
                Codec: codec,
                Container: container,
                BitrateKbps: activeFormat is { Bitrate: > 0 } ? activeFormat.Bitrate / 1000 : null,
                Itag: activeFormat?.Itag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller switched tracks — abort the whole resolve
        }
        catch (OperationCanceledException)
        {
            session?.Cancel();
            SabrStreamRegistry.RemoveFailed(config.VideoId);
            Trace("[Resolve] SABR pre-flight timed out (no data within 12s)");
            attempt.Outcome("SABR: timed out waiting for the first segment");
            return null;
        }
        catch (Exception ex)
        {
            session?.Cancel();
            SabrStreamRegistry.RemoveFailed(config.VideoId);
            Trace($"[Resolve] SABR playback failed: {ex.Message}");
            attempt.Outcome($"SABR: {ex.Message}");
            return null;
        }
    }

    private async Task<SabrSessionConfig?> RefreshSabrConfigAsync(
        string videoId, string? clientName, bool allowAuth, long fromTimeMs, CancellationToken cancellationToken)
    {
        try
        {
            Trace($"[Resolve] SABR continuation: re-resolving {videoId} from {fromTimeMs}ms" +
                  (clientName != null ? $" ({clientName}{(allowAuth ? "" : " (anon)")})" : " (full chain)"));

            var attempt = new ResolveAttempt(videoId);
            (ResolvedMedia? media, SabrSessionConfig? sabr, _) =
                await GetFastStreamUrlAsync(videoId, attempt, cancellationToken,
                        singleClientName: clientName, singleAllowAuth: allowAuth)
                    .ConfigureAwait(false);

            if (sabr is null && media is null && clientName != null)
            {
                attempt = new ResolveAttempt(videoId);
                (media, sabr, _) = await GetFastStreamUrlAsync(videoId, attempt, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (sabr is null)
            {
                Trace(media is null
                    ? "[Resolve] SABR continuation: the refreshed chain offered no SABR session"
                    : "[Resolve] SABR continuation: the refreshed chain offered a direct URL instead of SABR — cannot splice into the running stream");
                return null;
            }

            if (sabr.ClientInfo.ClientName is 1 or 2 or 56)
            {
                string descrambledUrl = await DescrambleSabrNParamAsync(sabr.ServerAbrStreamingUrl, cancellationToken).ConfigureAwait(false);
                if (descrambledUrl != sabr.ServerAbrStreamingUrl)
                    sabr = sabr with { ServerAbrStreamingUrl = descrambledUrl };

                // v8.6: videoId-bound token only — see TrySabrPlaybackAsync.
                string? contentPoToken = await GetGvsPoTokenAsync(videoId, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(sabr.PoToken) && !string.IsNullOrEmpty(contentPoToken))
                    sabr = sabr with { PoToken = contentPoToken };
            }

            return sabr;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the session is being torn down
        }
        catch (Exception ex)
        {
            Trace($"[Resolve] SABR continuation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// v8.1: the innertube client NAME to re-resolve with on a SABR
    /// continuation — the SAME client that produced the session (WEB stays
    /// WEB). Null when the client is no longer in the chain (e.g. a
    /// pre-v8.1 ANDROID session) — the refresher then falls back to the
    /// full chain.
    /// </summary>
    private static string? SabrRefreshClientName(int clientName) => clientName switch
    {
        1 => "WEB",
        2 => "MWEB",
        56 => "WEB_EMBEDDED_PLAYER",
        7 => "TVHTML5",
        101 => "VISIONOS",
        _ => null,
    };

    // ─────────────────────────────────────────────────────────────────────
    //  v8.0: GVS PO token for SABR sessions (built-in generator + external
    //  provider fallback — bgutil-ytdlp-pot-provider)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// v8.1: the GVS PO token for a SABR session, by precedence:
    ///   1. the BUILT-IN on-device generator (Android) — content-bound
    ///      (video ID) WebPO tokens minted in a hidden WebView via the
    ///      youtube.com web player's own BotGuard attestation flow;
    ///   2. a manually imported ***PO TOKEN*** pair (fallback — e.g. the
    ///      generator is disabled or cooling down after failures);
    ///   3. an optional bgutil provider server.
    /// v8.0 put the manual pair FIRST, which let a stale hidden token
    /// silently shadow the on-device mint forever (the field logs showed
    /// zero "[PoToken] minted" lines and an unattested stream). The built-in
    /// mint is fresh per video and must go first. Any failure is non-fatal —
    /// null just means the session streams unattested and the continuation
    /// carries it across the quota windows.
    /// </summary>
    private static async Task<string?> GetGvsPoTokenAsync(string contentBinding, CancellationToken cancellationToken)
    {
        // 0. An external bgutil provider, when configured, wins over everything:
        // it is an explicit, deliberate configuration (and the ground-truth
        // control used to separate the on-device WebView mint from GVS's
        // verdict). contentBinding is either a video ID (content-bound, the
        // /player + &pot= token) or a visitorData blob (session-bound).
        if (ProviderConfigured)
        {
            string? provided = await GetProviderGvsPoTokenAsync(contentBinding, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(provided))
            {
                Trace($"[Resolve] Using the provider PO token for {contentBinding} ({provided.Length} chars)");
                return provided;
            }
        }

        // 1. The built-in on-device generator (Android; no-op elsewhere).
        if (BuiltInPoTokenProvider.IsActive)
        {
            try
            {
                string? builtin = await BuiltInPoTokenProvider
                    .MintContentBoundTokenAsync(contentBinding, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(builtin))
                {
                    Trace($"[Resolve] Using the built-in on-device PO token for {contentBinding} ({builtin.Length} chars)");
                    return builtin;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace($"[Resolve] built-in PO token generator failed: {ex.Message}");
            }
        }

        // 2. A manually imported ***PO TOKEN*** pair.
        if (!string.IsNullOrEmpty(PoToken))
        {
            Trace("[Resolve] Using the manually imported PO token");
            return PoToken;
        }

        // 3. An external bgutil-ytdlp-pot-provider server (only reachable when
        // configured; step 0 already tried it, this covers the "configured but
        // step 0 was skipped" ordering).
        return ProviderConfigured
            ? await GetProviderGvsPoTokenAsync(contentBinding, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Base URL of an optional PO-token provider server — device-local
    /// configuration, NOT account auth (pushed by PoTokenProviderUrlSetting,
    /// survives account switches). When set, GVS PO tokens are minted on
    /// demand and attached to SABR streaming requests, lifting the ~60s
    /// unattested-stream cutoff so SABR tracks stream full-length without
    /// continuation re-buffers. Example: http://192.168.1.10:4416
    /// </summary>
    public static string? PoTokenProviderUrl { get; set; }

    /// <summary>
    /// Test-only switch (adb: <c>-a MUSIC_ACTION_PLAY_VIDEO --es mode sabr</c>):
    /// ignore EVERY direct URL (both plain and ciphered, plus the
    /// YoutubeExplode fallback) so the resolver must take the SABR path.
    /// Direct URLs for a PO-token-enforced video are intermittent — the same
    /// TVHTML5 URL 403s in one run and streams in the next — so a SABR
    /// experiment is otherwise non-deterministic. Implied by the <c>sabr</c>
    /// alias in <see cref="ClientsForTest"/>.
    /// </summary>
    public static bool ForceSabrForTest { get; set; }

    /// <summary>
    /// Test-only switch (adb: <c>--es clients tv</c>, <c>--es clients sabr</c>,
    /// comma-separated for several): replace the production fallback chain
    /// with EXACTLY those clients (see the alias list on
    /// <see cref="BuildTestClientChain"/>). null/empty keeps the full chain.
    /// Lets one transport be tested in isolation instead of whichever client
    /// the chain happens to reach first — <c>--es clients tv</c> for TVHTML5's
    /// direct URLs, <c>--es clients sabr</c> for the anonymous WEB/MWEB SABR
    /// session.
    /// </summary>
    public static string? ClientsForTest { get; set; }

    /// <summary>
    /// Test-only switch (adb: <c>--es cache off</c>, set together with
    /// <see cref="TrackCacheService.BypassCacheForTest"/>): never answer a
    /// resolve from — or write it into — the 2-hour in-memory ResolvedMedia
    /// cache. The app process survives <c>am start</c>, so without this a
    /// URL resolved by a different client in an earlier run would keep
    /// answering the per-client experiment.
    /// </summary>
    public static bool BypassUrlCacheForTest { get; set; }

    /// <summary>
    /// Test-only switch (adb: <c>--es binding visitor</c>): send the
    /// visitorData-bound GVS mint in StreamerContext.po_token (and MWEB's
    /// &amp;pot=) instead of the default content-bound (videoId) one. Both
    /// variants have now been rejected by GVS for this video, so it stays off
    /// unless a run explicitly asks for it.
    /// </summary>
    public static bool UseVisitorBoundTokens { get; set; }

    /// <summary>
    /// Test-only switch (adb: <c>--es identity anon</c>): drop every
    /// authenticated client from the resolve chain, so the SABR session is
    /// created by an anonymous /player call (see the SABR POST's cookie
    /// mismatch in SabrStreamSession).
    /// </summary>
    public static bool ForceAnonymousForTest { get; set; }

    /// <summary>
    /// Set when an external PO-token provider is configured. A provider token
    /// is not minted by the WebView, so the WebView's own VISITOR_DATA must not
    /// be pushed into the /player requests (the provider mints against the
    /// visitorData the session already has).
    /// </summary>
    private static bool ProviderConfigured => !string.IsNullOrWhiteSpace(PoTokenProviderUrl);

    /// <summary>
    /// True when SOME PO-token source is available for the anonymous
    /// WEB-family clients (on-device generator, a manual pair, or a provider).
    /// </summary>
    private static bool HasPoTokenSource =>
        BuiltInPoTokenProvider.IsActive || ProviderConfigured || !string.IsNullOrEmpty(PoToken);

    private static readonly HttpClient PoTokenProviderHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // bgutil's /get_pot is a per-content_binding mint: a video ID yields the
    // content-bound (GVS/&pot=) token, a visitorData blob yields the
    // session-bound one. Cache each binding separately.
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _providerTokenCache = new();

    /// <summary>
    /// Mints (or reuses a cached) GVS PO token from the configured provider:
    /// POST {PoTokenProviderUrl}/get_pot {"content_binding": …} (the current
    /// bgutil-ytdlp-pot-provider API; older builds called the field
    /// "visitor_data"). A VIDEO ID content binding yields the content-bound
    /// token the web player attaches to /player and &amp;pot=; a visitorData
    /// blob yields the session-bound (GVS) one. Any failure just returns null
    /// — the SABR continuation then carries playback across the quota windows
    /// instead.
    /// </summary>
    private static async Task<string?> GetProviderGvsPoTokenAsync(
        string contentBinding, CancellationToken cancellationToken)
    {
        string? baseUrl = PoTokenProviderUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;   // no provider configured
        if (string.IsNullOrWhiteSpace(contentBinding)) return null;

        if (_providerTokenCache.TryGetValue(contentBinding, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using HttpRequestMessage request = new(
                HttpMethod.Post, baseUrl.TrimEnd('/') + "/get_pot");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { content_binding = contentBinding }),
                Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await PoTokenProviderHttp.SendAsync(request, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false); } catch { }
                if (body.Length > 300) body = body[..300];
                Trace($"[Resolve] PO token provider returned HTTP {(int)response.StatusCode} " +
                      $"for binding {contentBinding} {body}");
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            JsonElement root = doc.RootElement;

            string? token =
                (root.TryGetProperty("poToken", out JsonElement t) ? t.GetString() : null) ??
                (root.TryGetProperty("po_token", out JsonElement t2) ? t2.GetString() : null);
            if (string.IsNullOrEmpty(token))
            {
                Trace("[Resolve] PO token provider response had no poToken");
                return null;
            }

            // expiresAt (ISO 8601 — bgutil-rs) or valid_until (unix seconds).
            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(6);
            if (root.TryGetProperty("expiresAt", out JsonElement ea) &&
                DateTimeOffset.TryParse(ea.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                expiresAt = parsed;
            }
            else if (root.TryGetProperty("valid_until", out JsonElement vu) &&
                     vu.ValueKind == JsonValueKind.Number)
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(vu.GetInt64());
            }
            // Never trust a provider TTL beyond 12h — observed GVS token
            // lifetimes vary from hours to weeks; 12h caps stale tokens
            // (a rejected one self-heals: the session drops it and the
            // continuation carries the stream).
            DateTimeOffset cap = DateTimeOffset.UtcNow.AddHours(12);
            if (expiresAt > cap) expiresAt = cap;

            _providerTokenCache[contentBinding] = (token!, expiresAt);

            Trace($"[Resolve] PO token minted by the provider for {contentBinding} " +
                  $"({token!.Length} chars, valid until {expiresAt:u})");
            return token;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace("[Resolve] PO token provider timed out");
            return null;
        }
        catch (Exception ex)
        {
            Trace($"[Resolve] PO token provider error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Captures everything a SABR session needs from a SABR-only player
    /// response: the signed serverAbrStreamingUrl, the base64url
    /// videoPlaybackUstreamerConfig (playerConfig.mediaCommonConfig.
    /// mediaUstreamerRequestConfig), and the audio formats (ordered by the
    /// same codec ranking as direct-URL selection). Null when the response
    /// does not carry a usable SABR session.
    /// </summary>
    private static SabrSessionConfig? TryBuildSabrConfig(
        JsonElement root, List<JsonElement> formats, InnerTubeClientInfo client,
        string videoId, string sabrUrl)
    {
        try
        {
            if (!root.TryGetProperty("playerConfig", out JsonElement playerConfig) ||
                !playerConfig.TryGetProperty("mediaCommonConfig", out JsonElement mediaCommon) ||
                !mediaCommon.TryGetProperty("mediaUstreamerRequestConfig", out JsonElement ustreamerRequest) ||
                !ustreamerRequest.TryGetProperty("videoPlaybackUstreamerConfig", out JsonElement ustreamerEl) ||
                ustreamerEl.GetString() is not string ustreamerConfig ||
                string.IsNullOrEmpty(ustreamerConfig))
            {
                Trace($"[Resolve] {client.Name}: SABR-only but no videoPlaybackUstreamerConfig — cannot stream");
                return null;
            }

            List<SabrAudioFormat> audio = new();
            foreach (JsonElement f in formats)
            {
                if (!f.TryGetProperty("mimeType", out JsonElement mimeEl) ||
                    mimeEl.GetString() is not string mime ||
                    !mime.Contains("audio/"))
                    continue;
                if (!f.TryGetProperty("itag", out JsonElement itagEl))
                    continue;

                // The ultra-low "saver" qualities never initialize over SABR
                // on the ANDROID-family clients (yt-dlp's broken-format note).
                if (f.TryGetProperty("audioQuality", out JsonElement audioQuality) &&
                    audioQuality.GetString() == "AUDIO_QUALITY_ULTRALOW")
                    continue;

                ulong lastModified = 0;
                if (f.TryGetProperty("lastModified", out JsonElement lm) &&
                    ulong.TryParse(lm.GetString(), out ulong lmt))
                    lastModified = lmt;

                string? xtags = f.TryGetProperty("xtags", out JsonElement xt) ? xt.GetString() : null;
                int bitrate = f.TryGetProperty("bitrate", out JsonElement br) ? br.GetInt32() : 0;

                audio.Add(new SabrAudioFormat(itagEl.GetInt32(), lastModified, xtags, mime, bitrate));
            }

            if (audio.Count == 0)
            {
                Trace($"[Resolve] {client.Name}: SABR-only response has no usable audio formats");
                return null;
            }

            // Same preference as direct-URL selection: opus > m4a, then bitrate.
            audio.Sort((a, b) =>
            {
                int rankA = MimeCodecRank(a.MimeType), rankB = MimeCodecRank(b.MimeType);
                return rankA != rankB ? rankB.CompareTo(rankA) : b.Bitrate.CompareTo(a.Bitrate);
            });

            string? vd = VisitorData;
            if (string.IsNullOrEmpty(vd) &&
                root.TryGetProperty("responseContext", out JsonElement rcEl) &&
                rcEl.TryGetProperty("visitorData", out JsonElement vdEl))
            {
                vd = vdEl.GetString();
            }

            SabrClientInfo clientInfo = new(
                ClientName: int.Parse(client.Id, CultureInfo.InvariantCulture),
                ClientVersion: client.Version,
                OsName: client.OsName,
                OsVersion: client.OsVersion,
                DeviceMake: client.Make,
                DeviceModel: client.Model,
                AndroidSdkVersion: client.Sdk != null && int.TryParse(client.Sdk, out int sdk) ? sdk : null,
                Hl: "en",
                Gl: "US",
                VisitorData: vd,
                UserAgent: EffectiveUserAgent(client));

            // PoToken stays null here — it is resolved AFTER capture via
            // GetGvsPoTokenAsync (TrySabrPlaybackAsync / the continuation
            // refresh). v8.0 pre-filled the manual pair, which let a stale
            // hidden token shadow the built-in on-device mint forever.
            return new SabrSessionConfig(
                VideoId: videoId,
                ServerAbrStreamingUrl: sabrUrl,
                UstreamerConfigB64: ustreamerConfig,
                ClientInfo: clientInfo,
                PreferredAudio: audio,
                PoToken: null,
                StartTimeMs: 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Resolve] SABR config capture failed: {ex.Message}");
            return null;
        }
    }

    private static int MimeCodecRank(string mime)
    {
        if (mime.Contains("opus")) return 3;
        if (mime.Contains("mp4a")) return 2;
        return 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: signatureTimestamp (v8.2 — the WEB-family player gate)
    // ─────────────────────────────────────────────────────────────────────────

    // Since YouTube's March 2026 player updates (yt-dlp #16212, YouTube.js,
    // FreeTube all hit it), /youtubei/v1/player demands a CURRENT
    // signatureTimestamp — the sts literal inside the LIVE base.js player
    // script — for WEB, WEB_EMBEDDED_PLAYER and TVHTML5, authenticated OR
    // anonymous. Missing/stale sts ⇒ UNPLAYABLE "The page needs to be
    // reloaded." — exactly what the v8.1 field logs showed for TVHTML5 and
    // WEB(anon). The sts is a plain literal (no JS engine needed); it is
    // fetched once per 6h from /iframe_api → base.js and cached. When a
    // client answers with the "reloaded" reason, the cache is dropped
    // (GetFastStreamUrlAsync) so the next client re-fetches the new one
    // (YouTube rolls a new base.js every few weeks).
    private static readonly SemaphoreSlim _stsLock = new(1, 1);
    private static (int Sts, string PlayerUrl, DateTimeOffset FetchedAt)? _cachedSts;
    private static readonly TimeSpan StsCacheTtl = TimeSpan.FromHours(6);

    [GeneratedRegex(@"player(?:\\/|/)([0-9a-fA-F]{6,16})(?:\\/|/)")]
    private static partial Regex PlayerIdRegex();

    [GeneratedRegex(@"(?:signatureTimestamp|sts)\s*:\s*(\d{5,})")]
    private static partial Regex StsRegex();

    private async Task<int?> GetSignatureTimestampAsync(CancellationToken cancellationToken)
    {
        if (_cachedSts is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < StsCacheTtl)
            return cached.Sts;

        await _stsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSts is { } again && DateTimeOffset.UtcNow - again.FetchedAt < StsCacheTtl)
                return again.Sts;

            string iframeApi = await _innerTubeClient
                .GetStringAsync("https://www.youtube.com/iframe_api", cancellationToken)
                .ConfigureAwait(false);

            Match idMatch = PlayerIdRegex().Match(iframeApi);
            if (!idMatch.Success)
            {
                Trace("[Resolve] Could not extract player ID from iframe_api");
                return _cachedSts?.Sts;
            }

            string playerId = idMatch.Groups[1].Value;
            string[] candidates =
            [
                $"https://www.youtube.com/s/player/{playerId}/player_ias.vflset/en_US/base.js",
                $"https://www.youtube.com/s/player/{playerId}/player_es6.vflset/en_US/base.js",
            ];

            foreach (string url in candidates)
            {
                try
                {
                    string js = await _innerTubeClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    Match stsMatch = StsRegex().Match(js);
                    if (stsMatch.Success && int.TryParse(stsMatch.Groups[1].Value, CultureInfo.InvariantCulture, out int sts))
                    {
                        _cachedSts = (sts, url, DateTimeOffset.UtcNow);
                        Trace($"[Resolve] Fetched signatureTimestamp={sts} (player {playerId})");
                        return sts;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* try next player variant */ }
            }

            Trace($"[Resolve] Could not extract signatureTimestamp for player {playerId}");
            return _cachedSts?.Sts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"[Resolve] signatureTimestamp fetch failed: {ex.Message}");
            return _cachedSts?.Sts;
        }
        finally
        {
            _stsLock.Release();
        }
    }

    private async Task<string> DescrambleSabrNParamAsync(string sabrUrl, CancellationToken ct)
    {
        string? nVal = GetQueryParamValue(sabrUrl, "n");
        if (string.IsNullOrEmpty(nVal)) return sabrUrl;

        string? playerUrl = _cachedSts?.PlayerUrl;
        if (string.IsNullOrEmpty(playerUrl))
        {
            await GetSignatureTimestampAsync(ct).ConfigureAwait(false);
            playerUrl = _cachedSts?.PlayerUrl;
        }
        if (string.IsNullOrEmpty(playerUrl)) return sabrUrl;

        string? solved = await BuiltInPoTokenProvider.SolveNChallengeAsync(playerUrl, nVal, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(solved) || solved == nVal) return sabrUrl;

        Trace($"[Resolve] Descrambled SABR n parameter ({nVal.Length} -> {solved.Length} chars)");
        return ReplaceQueryParamValue(sabrUrl, "n", solved);
    }

    private async Task<int> ProbeStreamUrlAsync(string url, string userAgent, CancellationToken cancellationToken)
    {
        if (!url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return 200;

        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using HttpResponseMessage resp = await _innerTubeClient.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return (int)resp.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return 200; // let ExoPlayer try on transient probe timeout
        }
    }

    private static string? GetQueryParamValue(string url, string name)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (string part in url[(q + 1)..].Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }

    private static string ReplaceQueryParamValue(string url, string name, string value)
    {
        int q = url.IndexOf('?');
        if (q < 0) return url + "?" + name + "=" + Uri.EscapeDataString(value);
        string prefix = url[..(q + 1)];
        string[] parts = url[(q + 1)..].Split('&');
        bool replaced = false;
        for (int i = 0; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq > 0 && parts[i][..eq].Equals(name, StringComparison.Ordinal))
            {
                parts[i] = name + "=" + Uri.EscapeDataString(value);
                replaced = true;
            }
        }
        string joined = string.Join('&', parts);
        return replaced ? prefix + joined : prefix + joined + "&" + name + "=" + Uri.EscapeDataString(value);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: request building
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// v8.4: MWEB sends the LIVE User-Agent of the platform WebView running
    /// BotGuard (captured in WebViewPoTokenProvider.CreateWebView) instead
    /// of the static client UA — the /player request then matches the
    /// attestation environment exactly (UA, Client Hints, platform).
    /// Every other client keeps its static yt-dlp UA.
    /// </summary>
    private static string EffectiveUserAgent(InnerTubeClientInfo client) =>
        client.Name == "MWEB" && !string.IsNullOrEmpty(BuiltInPoTokenProvider.WebViewUserAgent)
            ? BuiltInPoTokenProvider.WebViewUserAgent
            : client.UA;

    private static async Task<HttpRequestMessage> BuildPlayerRequestAsync(
        string videoId, InnerTubeClientInfo client, bool allowAuth = true)
    {
        string baseUrl = client.Name.Contains("MUSIC") || client.Name == "WEB_REMIX"
            ? "https://music.youtube.com"
            : client.Name == "MWEB"
                ? "https://m.youtube.com"
                : "https://www.youtube.com";

        string apiUrl = $"{baseUrl}/youtubei/v1/player?key={InnerTubeApiKey}&prettyPrint=false";

        HttpRequestMessage request = new(HttpMethod.Post, apiUrl);
        // WebEmbedded's UA ends in "Safari/605.1.15,gzip(gfe)" — the exact UA
        // yt-dlp ships for that client — and .NET's validated header parser
        // rejects the ",gzip(gfe)" suffix (ProductInfoHeaderValue syntax).
        // TryAddWithoutValidation sends the UA byte-for-byte as yt-dlp does.
        request.Headers.TryAddWithoutValidation("User-Agent", EffectiveUserAgent(client));
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-YouTube-Client-Name", client.Id);
        request.Headers.Add("X-YouTube-Client-Version", client.Version);

        // X-Origin + Referer for WEB-family and VISIONOS clients (yt-dlp sends
        // Origin on every innertube API call, so the new TVHTML5 client gets it too)
        bool needsOrigin = client.Name.StartsWith("WEB") || client.Name == "MWEB" || client.Name == "VISIONOS" || client.Name == "TVHTML5";
        if (needsOrigin)
        {
            request.Headers.Add("X-Origin", baseUrl);
            request.Headers.Add("Referer", $"{baseUrl}/");
        }

        // ── Cookie authentication ────────────────────────────────────────────
        // Cookies are attached to the cookie-supporting clients: the WEB family
        // (WEB, WEB_EMBEDDED_PLAYER) and the Samsung TV client (TVHTML5 —
        // SUPPORTS_COOKIES in yt-dlp's client table). VISIONOS stays
        // anonymous — sending cookies to it confuses YouTube and causes
        // "no streamingData" responses. v8.1: allowAuth=false is the strictly
        // ANONYMOUS variant of a client (the "WEB (anon)" chain entry — it
        // dodges the cookie "The page needs to be reloaded." rejection and
        // yields the Web-family SABR session the built-in tokens attach to).
        string? cookie = Cookies;
        bool isWebClient = client.Name.StartsWith("WEB") || client.Name == "MWEB";
        bool supportsCookies = allowAuth && (isWebClient || client.Name == "TVHTML5");
        bool hasCookiesForThisClient = !string.IsNullOrEmpty(cookie) && supportsCookies;

        // Don't send visitorData with cookie-authenticated requests — stale
        // visitorData causes "The page needs to be reloaded" errors.
        // OAuth requests DO carry visitorData (mirrors ytmusicapi).
        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd) && !hasCookiesForThisClient)
            request.Headers.Add("X-Goog-Visitor-Id", vd);

        // OAuth Bearer (device-flow login) takes priority over cookies when
        // both exist; cookies + SAPISIDHASH remain the fallback.
        if (allowAuth && isWebClient && !hasCookiesForThisClient && OAuthTokenProvider != null)
        {
            string? bearer = await OAuthTokenProvider();
            if (!string.IsNullOrEmpty(bearer))
            {
                request.Headers.Add("Authorization", $"Bearer {bearer}");
                Debug.WriteLine($"[Resolve] Attached OAuth Bearer for {client.Name} client");
                return request;
            }
        }

        if (hasCookiesForThisClient)
        {
            request.Headers.Add("Cookie", cookie);

            string? sapisidHash = GenerateSapisidHash(cookie!, baseUrl);
            if (sapisidHash != null)
            {
                request.Headers.Add("Authorization", sapisidHash);
                Debug.WriteLine($"[Resolve] Attached cookies + SAPISIDHASH for {client.Name} client");
            }
        }

        return request;
    }

    /// <summary>
    /// Generates the SAPISIDHASH authorization header.
    /// SHA1(timestamp + " " + sapisid + " " + origin) → "SAPISIDHASH {timestamp}_{hash}"
    /// </summary>
    public static string? GenerateSapisidHash(string cookieString, string origin)
    {
        string? sapisid = null;
        foreach (string part in cookieString.Split(';', '&'))
        {
            string trimmed = part.Trim();

            if (trimmed.StartsWith("__Secure-3PAPISID=", StringComparison.OrdinalIgnoreCase))
            {
                sapisid = trimmed["__Secure-3PAPISID=".Length..].Trim();
                break; // prefer __Secure-3PAPISID
            }
            if (trimmed.StartsWith("SAPISID=", StringComparison.OrdinalIgnoreCase))
            {
                sapisid = trimmed["SAPISID=".Length..].Trim();
                // don't break — keep looking for __Secure-3PAPISID
            }
        }

        if (string.IsNullOrEmpty(sapisid))
            return null;

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string input = $"{timestamp} {sapisid} {origin}";

        using SHA1 sha1 = SHA1.Create();
        byte[] hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input));
        string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return $"SAPISIDHASH {timestamp}_{hash}";
    }

    private static HttpContent BuildPlayerPayload(
        string videoId, InnerTubeClientInfo client, bool allowAuth = true, int? signatureTimestamp = null,
        string? playerPoToken = null)
    {
        Dictionary<string, string> clientObj = new()
        {
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["hl"] = "en",
            ["gl"] = "US"
        };

        if (client.OsName != null) clientObj["osName"] = client.OsName;
        if (client.OsVersion != null) clientObj["osVersion"] = client.OsVersion;
        if (client.Make != null) clientObj["deviceMake"] = client.Make;
        if (client.Model != null) clientObj["deviceModel"] = client.Model;
        if (client.Sdk != null) clientObj["androidSdkVersion"] = client.Sdk;

        // Don't include visitorData for cookie-authenticated requests.
        // Cookies go to the WEB family + TVHTML5 — VISIONOS is anonymous.
        bool supportsCookies = allowAuth && (client.Name.StartsWith("WEB") || client.Name == "MWEB" || client.Name == "TVHTML5");
        bool cookieAuth = !string.IsNullOrEmpty(Cookies) && supportsCookies;

        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd) && !cookieAuth)
            clientObj["visitorData"] = vd;

        // v6 payload upgrades (all mirrored from yt-dlp's player request):
        //   contentCheckOk / racyCheckOk — bypass the content-warning gate
        //     that otherwise makes borderline videos UNPLAYABLE;
        //   playbackContext.contentPlaybackContext.html5Preference — what
        //     every real client sends.
        // v8.2: contentPlaybackContext.signatureTimestamp for the WEB family
        // and TVHTML5 — the sts of the LIVE base.js player. Missing/stale sts
        // ⇒ UNPLAYABLE "The page needs to be reloaded." (March 2026 gate).
        Dictionary<string, object> contentPlaybackContext = new()
        {
            ["html5Preference"] = "HTML5_PREF_WANTS"
        };
        if (signatureTimestamp is int sts &&
            (client.Name.StartsWith("WEB") || client.Name == "MWEB" || client.Name == "TVHTML5"))
        {
            contentPlaybackContext["signatureTimestamp"] = sts;
        }

        Dictionary<string, object> payload = new()
        {
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
            ["playbackContext"] = new Dictionary<string, object>
            {
                ["contentPlaybackContext"] = contentPlaybackContext
            },
            ["context"] = new Dictionary<string, object>
            {
                ["client"] = clientObj,
                ["user"] = new Dictionary<string, object>()
            }
        };

        // GVS PO token (bot attestation): attached to the WEB family and MWEB,
        // whose format URLs / SABR sessions are gated behind one (yt-dlp's
        // PO-token guide). Two sources:
        //   - v8.1: a manually imported ***PO TOKEN*** pair, attached only
        //     while the built-in generator is NOT active;
        //   - v8.8: the token minted for THIS video before the /player call
        //     (playerPoToken) — an unattested /player still hands out a
        //     serverAbrStreamingUrl that GVS downgrades to StreamProtectionStatus
        //     2, so the anonymous MWEB/WEB session must be attested at issue time.
        string? effectivePoToken = !string.IsNullOrEmpty(playerPoToken)
            ? playerPoToken
            : (!BuiltInPoTokenProvider.IsActive ? PoToken : null);

        if (!string.IsNullOrEmpty(effectivePoToken) &&
            (client.Name.StartsWith("WEB", StringComparison.Ordinal) || client.Name == "MWEB"))
        {
            payload["serviceIntegrityDimensions"] = new Dictionary<string, object>
            {
                ["poToken"] = effectivePoToken
            };
            Trace(!string.IsNullOrEmpty(playerPoToken)
                ? $"[Resolve] Attached minted GVS PO token to {client.Name} /player payload"
                : $"[Resolve] Attached manual GVS PO token to {client.Name} payload");
        }

        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: stream selection (codec-aware: opus > mp4a > other)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The chosen audio format: the stream URL plus the codec/bitrate identity
    /// shown in the Playback settings diagnostics panel.
    /// </summary>
    private sealed record AudioChoice(
        string Url, int Itag, string MimeType, int Bitrate, int SampleRate,
        bool IsUltraLow = false)
    {
        public ResolvedMedia ToResolvedMedia(string url, string userAgent, string streamType, string client)
        {
            (string? codec, string? container) = SplitMimeType(MimeType);
            return new ResolvedMedia(
                url, userAgent,
                Source: "YouTube",
                StreamType: streamType,
                Client: client,
                Codec: codec,
                Container: container,
                BitrateKbps: Bitrate > 0 ? Bitrate / 1000 : null,
                SampleRateHz: SampleRate > 0 ? SampleRate : null,
                Itag: Itag > 0 ? Itag : null);
        }
    }

    /// <summary>Best-effort format hint from a direct media URL's extension.</summary>
    private static (string? Codec, string? Container) DescribeUrlFormat(string url)
    {
        string path = url;
        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        int slash = path.LastIndexOf('/');
        if (slash >= 0) path = path[(slash + 1)..];
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return (null, null);

        string extension = path[(dot + 1)..].ToLowerInvariant();
        return extension switch
        {
            "m4a" => ("mp4a", "m4a"),
            "mp4" => ("mp4a", "mp4"),
            "aac" => ("mp4a", "aac"),
            "webm" => ("opus", "webm"),
            "opus" => ("opus", "opus"),
            "ogg" or "oga" => ("vorbis", "ogg"),
            "mp3" => ("mp3", "mp3"),
            "flac" => ("flac", "flac"),
            "wav" => ("pcm", "wav"),
            _ => (null, null),
        };
    }

    /// <summary>Splits "audio/webm; codecs=\"opus\"" into codec and container labels.</summary>
    private static (string? Codec, string? Container) SplitMimeType(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return (null, null);

        string? container = null;
        int slash = mime.IndexOf('/');
        if (slash >= 0)
        {
            int end = mime.IndexOf(';', slash);
            container = (end < 0 ? mime[(slash + 1)..] : mime[(slash + 1)..end]).Trim();
        }

        string? codec = null;
        int codecsIndex = mime.IndexOf("codecs=", StringComparison.OrdinalIgnoreCase);
        if (codecsIndex >= 0)
            codec = mime[(codecsIndex + 7)..].Trim().Trim('"', '\'', ' ');

        return (string.IsNullOrEmpty(codec) ? null : codec, string.IsNullOrEmpty(container) ? null : container);
    }

    /// <summary>
    /// v9.4: true when <paramref name="candidate"/> should replace the current
    /// best audio pick. A normal-quality format always beats the "saver" tier
    /// (AUDIO_QUALITY_ULTRALOW, itag 599 at ~31 kbps) even when the saver
    /// format ranks higher by codec/bitrate — 599 is still taken when it is
    /// the only audio the client offers, so nothing regresses.
    /// </summary>
    private static bool IsBetterAudio(
        int rank, int bitrate, int sampleRate, bool ultraLow,
        int bestRank, int bestBitrate, int bestSampleRate, bool bestUltraLow)
    {
        if (ultraLow != bestUltraLow)
            return bestUltraLow;
        return rank > bestRank ||
               (rank == bestRank && bitrate > bestBitrate) ||
               (rank == bestRank && bitrate == bestBitrate && sampleRate > bestSampleRate);
    }

    private static bool IsUltraLowAudio(JsonElement format) =>
        format.TryGetProperty("audioQuality", out JsonElement quality) &&
        quality.GetString() == "AUDIO_QUALITY_ULTRALOW";

    /// <summary>
    /// Finds the best audio format with a direct (non-ciphered) URL.
    /// Enumerates the list once, picks by codec rank → bitrate → sample rate.
    /// </summary>
    private static AudioChoice? FindBestAudioWithUrl(List<JsonElement> formats)
    {
        JsonElement best = default;
        int bestRank = 0, bestBitrate = 0, bestSampleRate = 0;
        bool bestUltraLow = false;
        bool found = false;

        foreach (var f in formats)
        {
            if (!f.TryGetProperty("mimeType", out JsonElement mime) ||
                !mime.GetString()!.Contains("audio/")) continue;
            if (!f.TryGetProperty("url", out _)) continue;

            int rank = CodecRank(f);
            int bitrate = f.TryGetProperty("bitrate", out JsonElement b) ? b.GetInt32() : 0;
            int sampleRate = f.TryGetProperty("audioSampleRate", out JsonElement sr) &&
                             int.TryParse(sr.GetString(), out int rate) ? rate : 0;
            bool ultraLow = IsUltraLowAudio(f);

            if (!found || IsBetterAudio(rank, bitrate, sampleRate, ultraLow,
                                        bestRank, bestBitrate, bestSampleRate, bestUltraLow))
            {
                best = f;
                bestRank = rank;
                bestBitrate = bitrate;
                bestSampleRate = sampleRate;
                bestUltraLow = ultraLow;
                found = true;
            }
        }

        return found && best.TryGetProperty("url", out JsonElement urlProp)
            ? ToAudioChoice(best, urlProp.GetString())
            : null;
    }

    private static AudioChoice? ToAudioChoice(JsonElement format, string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        string mime = format.TryGetProperty("mimeType", out JsonElement mimeEl)
            ? mimeEl.GetString() ?? ""
            : "";
        int itag = format.TryGetProperty("itag", out JsonElement itagEl) ? itagEl.GetInt32() : 0;
        int bitrate = format.TryGetProperty("bitrate", out JsonElement b) ? b.GetInt32() : 0;
        int sampleRate = format.TryGetProperty("audioSampleRate", out JsonElement sr) &&
                         int.TryParse(sr.GetString(), out int rate) ? rate : 0;
        bool isUltraLow = format.TryGetProperty("audioQuality", out JsonElement q) &&
                          q.GetString() == "AUDIO_QUALITY_ULTRALOW";

        return new AudioChoice(url, itag, mime, bitrate, sampleRate, isUltraLow);
    }

    /// <summary>
    /// v8.7: best audio among the CIPHERED formats, deciphered on the fly
    /// via the built-in EJS engine: parse the signatureCipher blob (url + s
    /// + sp), solve s (signature) and n (throttling) in ONE EJS run, splice
    /// the results back into the URL. Returns null when no ciphered audio
    /// exists, the player script URL is unknown, or the solver fails — the
    /// caller then falls back to the SABR / YoutubeExplode paths. The solver
    /// results are cached per (playerUrl, s) / (playerUrl, n) in the
    /// WebView provider, so the per-format cost is one WebView eval after
    /// the first track.
    /// </summary>
    private async Task<AudioChoice?> FindBestAudioWithCipherAsync(
        List<JsonElement> formats, CancellationToken cancellationToken, string? poToken = null,
        IReadOnlySet<int>? skipItags = null)
    {
        JsonElement best = default;
        int bestRank = 0, bestBitrate = 0, bestSampleRate = 0;
        bool bestUltraLow = false;
        bool found = false;

        foreach (var f in formats)
        {
            if (!f.TryGetProperty("mimeType", out JsonElement mime) ||
                !mime.GetString()!.Contains("audio/")) continue;
            if (!f.TryGetProperty("signatureCipher", out _)) continue;
            if (skipItags != null && f.TryGetProperty("itag", out JsonElement skipItag) &&
                skipItags.Contains(skipItag.GetInt32())) continue;

            int rank = CodecRank(f);
            int bitrate = f.TryGetProperty("bitrate", out JsonElement b) ? b.GetInt32() : 0;
            int sampleRate = f.TryGetProperty("audioSampleRate", out JsonElement sr) &&
                             int.TryParse(sr.GetString(), out int rate) ? rate : 0;
            bool ultraLow = IsUltraLowAudio(f);

            if (!found || IsBetterAudio(rank, bitrate, sampleRate, ultraLow,
                                        bestRank, bestBitrate, bestSampleRate, bestUltraLow))
            {
                best = f;
                bestRank = rank;
                bestBitrate = bitrate;
                bestSampleRate = sampleRate;
                bestUltraLow = ultraLow;
                found = true;
            }
        }

        if (!found || !best.TryGetProperty("signatureCipher", out JsonElement cipherProp) ||
            cipherProp.GetString() is not string cipher || string.IsNullOrEmpty(cipher))
            return null;

        string? rawUrl = null, sVal = null, sp = "signature";
        foreach (string part in cipher.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq];
            string val = WebUtility.UrlDecode(part[(eq + 1)..]);
            if (key == "url") rawUrl = val;
            else if (key == "s") sVal = val;
            else if (key == "sp" && !string.IsNullOrEmpty(val)) sp = val;
        }
        if (string.IsNullOrEmpty(rawUrl) || string.IsNullOrEmpty(sVal))
            return null;

        string? playerUrl = _cachedSts?.PlayerUrl;
        if (string.IsNullOrEmpty(playerUrl))
        {
            await GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false);
            playerUrl = _cachedSts?.PlayerUrl;
        }
        if (string.IsNullOrEmpty(playerUrl))
            return null;

        string? nVal = GetQueryParamValue(rawUrl, "n");
        (string? solvedSig, string? solvedN) =
            await BuiltInPoTokenProvider.SolveChallengesAsync(playerUrl, sVal, nVal, cancellationToken)
                .ConfigureAwait(false);

        if (string.IsNullOrEmpty(solvedSig))
            return null;

        string finalUrl = ReplaceQueryParamValue(rawUrl, sp, solvedSig);
        if (!string.IsNullOrEmpty(solvedN) && solvedN != nVal)
            finalUrl = ReplaceQueryParamValue(finalUrl, "n", solvedN);

        // v8.9: MWEB's HTTPS formats are gated on &pot= (yt-dlp's MWEB row:
        // PO token required for GVS). The URL is signed over its other params,
        // so appending pot last is what the client does too.
        bool potAppended = false;
        if (!string.IsNullOrEmpty(poToken) && GetQueryParamValue(finalUrl, "pot") is null)
        {
            finalUrl = ReplaceQueryParamValue(finalUrl, "pot", poToken);
            potAppended = true;
        }

        // v9.4: log the SOLVED sig length next to the raw one. The player's
        // sig function permutes the decoded bytes, so a correct solve keeps the
        // length; a mismatch means EJS returned a wrong/partial result and the
        // URL is guaranteed to 403 (field logs: raw-s 104 chars never worked).
        Trace($"[Resolve] deciphered signatureCipher audio (sig {sVal.Length}->{solvedSig.Length} chars, n {(nVal?.Length ?? 0)} -> {(solvedN?.Length ?? 0)}{(potAppended ? ", &pot= appended" : "")})");
        return ToAudioChoice(best, finalUrl);
    }

    private static int CodecRank(JsonElement format)
    {
        if (!format.TryGetProperty("mimeType", out JsonElement mimeEl)) return 0;
        string mime = mimeEl.GetString() ?? "";
        if (mime.Contains("opus")) return 3;
        if (mime.Contains("mp4a")) return 2;
        return 1;
    }

    private static int CodecRank(AudioOnlyStreamInfo stream)
    {
        string codec = stream.AudioCodec.ToLowerInvariant();
        if (codec.Contains("opus")) return 3;
        if (codec.Contains("mp4a")) return 2;
        return 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  InnerTube client definitions
    // ─────────────────────────────────────────────────────────────────────────

    private static class InnerTubeClients
    {
        public static readonly InnerTubeClientInfo VisionOS = new()
        {
            Name = "VISIONOS",
            Version = "1.02",
            Id = "101",
            OsName = "visionOS",
            OsVersion = "26.5.23O471",
            Make = "Apple",
            Model = "RealityDevice17,1",
            Sdk = null,
            UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15"
        };

        public static readonly InnerTubeClientInfo Web = new()
        {
            Name = "WEB",
            Version = "2.20260819.00.00",
            Id = "1",
            OsName = null,
            OsVersion = null,
            Make = null,
            Model = null,
            Sdk = null,
            UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        };

        // mweb — the mobile web client (clientName 2, m.youtube.com): the
        // SABR client whose environment MATCHES the Android WebView that
        // mints the built-in BotGuard token (mobile Chrome UA is genuine
        // there). EffectiveUserAgent swaps in the LIVE WebView UA at request
        // time, so /player + StreamerContext match the attestation 1:1.
        public static readonly InnerTubeClientInfo Mweb = new()
        {
            Name = "MWEB",
            Version = "2.20260819.00.00",
            Id = "2",
            OsName = "Android",
            OsVersion = null,
            Make = null,
            Model = null,
            Sdk = null,
            UA = MwebUserAgent
        };

        // tv_samsung — yt-dlp's #1 default client for EVERYONE (Oct 2026):
        // plain TVHTML5 clientName + Samsung Tizen device fields. No PO token
        // required, no GVS policy entry, returns direct URLs, SUPPORTS_COOKIES.
        public static readonly InnerTubeClientInfo TvSamsung = new()
        {
            Name = "TVHTML5",
            Version = "5.20260707",
            Id = "7",
            OsName = "Tizen",
            OsVersion = "2.4.0",
            Make = "Samsung",
            Model = "UKS9800",
            Sdk = null,
            UA = "Mozilla/5.0 (SMART-TV; Linux; Tizen 2.4.0) AppleWebKit/538.1 (KHTML, like Gecko) Version/2.4.0 TV Safari/538.1"
        };

        // web_embedded — yt-dlp's #1 AUTHENTICATED default (Oct 2026). Only
        // plays embeddable videos, but often returns direct URLs where the
        // plain WEB client is SABR-only. Cookies + SAPISIDHASH are attached
        // (WEB family).
        public static readonly InnerTubeClientInfo WebEmbedded = new()
        {
            Name = "WEB_EMBEDDED_PLAYER",
            Version = "2.20260708.00.00",
            Id = "56",
            OsName = null,
            OsVersion = null,
            Make = null,
            Model = null,
            Sdk = null,
            UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)"
        };

        // NOTE: TVHTML5_SIMPLY_EMBEDDED_PLAYER was removed — it returns
        // "YouTube is no longer supported in this application or device."
        // as of Aug 2026. Do not re-add it.
        //
        // NOTE: ANDROID_VR was deliberately NOT added (yt-dlp client notes,
        // Oct 2026): version 1.65.10 gets ALL formats 403'd since 2026.08.17,
        // and clientVersion > 1.65 returns SABR-only.

        // v8.1: ANDROID was REMOVED from the playback chain — it answers
        // SABR-only and GVS demands a DroidGuard attestation token for it,
        // which no third-party app can mint (Play-Services-bound: the token
        // is issued to com.google.android.youtube's own package + signing
        // key). Kept for reference; do NOT re-add it to the chain.
        public static readonly InnerTubeClientInfo Android = new()
        {
            Name = "ANDROID",
            Version = "21.26.364",
            Id = "3",
            OsName = "Android",
            OsVersion = "11",
            Make = "Google",
            Model = "Pixel 8 Pro",
            Sdk = "30",
            UA = "com.google.android.youtube/21.26.364 (Linux; U; Android 11; en_US; Pixel 8 Pro; Build/RQ3A.211001.001) gzip"
        };
    }

    private sealed class InnerTubeClientInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string? OsName { get; init; }
        public string? OsVersion { get; init; }
        public string? Make { get; init; }
        public string? Model { get; init; }
        public string? Sdk { get; init; }
        public string UA { get; init; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Immutable auth state
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads Set-Cookie headers from an innertube response and reports the
    /// rotating session cookies (only __Secure-*PSIDTS — the short-lived ones
    /// YouTube rotates on nearly every response; the long-lived SID/PSID
    /// cookies stay valid on their own).
    /// </summary>
    private static void HarvestSessionCookies(HttpResponseMessage response)
    {
        try
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
                return;

            foreach (string header in values)
            {
                int eq = header.IndexOf('=');
                if (eq <= 0) continue;

                string name = header[..eq].Trim();
                if (!name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith("PSIDTS", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = header[(eq + 1)..];
                int sc = value.IndexOf(';');
                if (sc > 0) value = value[..sc];

                if (!string.IsNullOrEmpty(value))
                {
                    Debug.WriteLine($"[Resolve] session cookie rotated: {name}");
                    SessionCookieRefreshed?.Invoke(name, value);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Resolve] cookie harvest failed: {ex.Message}");
        }
    }

    private sealed record PlaybackAuthState(
        string? Cookie,
        string? VisitorData,
        string? DataSyncId,
        string? PoToken)
    {
        public static readonly PlaybackAuthState Empty = new(null, null, null, null);
    }
}