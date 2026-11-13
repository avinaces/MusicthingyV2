using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services.Sabr;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
// Client chain (v6, Nov 2026 — mirrors yt-dlp 2026.10.26 production defaults):
//   anonymous: VISIONOS → TVHTML5 (Samsung TV) → ANDROID (+GVS PO token if set)
//   authed:    VISIONOS → TVHTML5 → WEB_EMBEDDED_PLAYER → WEB → ANDROID
//   yt-dlp's own production defaults, for reference (2026.10.26):
//     anonymous: tv_samsung → visionos → web
//     authed:    web_embedded → tv_samsung → web
//     premium:   web_creator → tv_samsung → web
//   Last: YoutubeExplode (with cookies if available) — cipher deciphering fallback.
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
// DataSource over the session's in-memory UMP stream). A GVS PO Token
// (Settings → PO Token) is attached to both the player requests and the SABR
// requests when set. See Docs/SABR_Research.md for the protocol details.
// ────────────────────────────────────────────────────────────────────────────

public partial class YouTubeResolverService
{
    // ── Constants ───────────────────────────────────────────────────────────
    private const double CacheExpiryHours = 2.0;
    private const int ManifestFetchTimeoutSeconds = 60;
    private const int VideoInfoFetchTimeoutSeconds = 10;
    private const int MaxClientAttempts = 2;
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

    // ── IPv4-only handler (shared singleton) ────────────────────────────────
    private static readonly SocketsHttpHandler _ipv4Handler = new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            IPAddress[] ipAddresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host,
                AddressFamily.InterNetwork,
                cancellationToken);

            if (ipAddresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(new IPEndPoint(ipAddresses[0], context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static SocketsHttpHandler Ipv4Handler => _ipv4Handler;

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

    // ── Per-client circuit breaker (10-min cooldown on HTTP 403) ─────────────
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();
    private static readonly TimeSpan FailedClientBackoff = TimeSpan.FromMinutes(10);

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
            _innerTubeClient = new HttpClient(_ipv4Handler) { Timeout = TimeSpan.FromSeconds(30) };

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
            return new ResolvedMedia(trackUrl, DefaultUserAgent);

        // Cache check
        if (_streamUrlCache.TryGetValue(trackUrl, out (ResolvedMedia Media, DateTime FetchedAt) cached)
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
            Trace($"[Resolve] HasCookies={HasCookies}, HasPoToken={!string.IsNullOrEmpty(PoToken)}, trying InnerTube clients for {videoId.Value.Value}");
            (resolvedMedia, SabrSessionConfig? sabrConfig) = await GetFastStreamUrlAsync(videoId.Value.Value, attempt, cancellationToken);

            // v7 (SABR): when every client only offered the video through
            // SABR (no direct URLs anywhere), stream it ourselves through the
            // UMP protocol client instead of failing. Android only — the
            // SabrDataSource is an ExoPlayer/Media3 construct; on Windows
            // the resolve chain behaves exactly as before.
            if (resolvedMedia == null && sabrConfig != null && OperatingSystem.IsAndroid())
            {
                Trace("[Resolve] All clients SABR-only — trying SABR streaming playback");
                resolvedMedia = await TrySabrPlaybackAsync(sabrConfig, attempt, cancellationToken);
            }

            // YoutubeExplode fallback — always try, even if InnerTube failed.
            // YoutubeExplode has its own cipher deciphering + watch-page scraping.
            if (resolvedMedia == null)
            {
                Trace("[Resolve] InnerTube chain failed — trying YoutubeExplode fallback");
                try
                {
                    YoutubeClient? yteClient = HasCookies
                        ? GetYoutubeClientWithCookies()
                        : _youtubeClient;

                    if (yteClient != null)
                    {
                        string? url = await Task.Run(async () =>
                        {
                            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromSeconds(ManifestFetchTimeoutSeconds));

                            StreamManifest manifest = await yteClient.Videos.Streams.GetManifestAsync(videoId.Value, cts.Token);

                            return manifest.GetAudioOnlyStreams()
                                .OrderByDescending(CodecRank)
                                .ThenByDescending(s => s.Bitrate)
                                .FirstOrDefault()?.Url;
                        }, cancellationToken);

                        if (!string.IsNullOrEmpty(url))
                        {
                            resolvedMedia = new ResolvedMedia(url, DefaultUserAgent);
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

        if (resolvedMedia.Url != trackUrl &&
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
                HttpRequestMessage request = await BuildPlayerRequestAsync(videoId.Value.Value, client);
                request.Content = BuildPlayerPayload(videoId.Value.Value, client);

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

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: client fallback chain
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(ResolvedMedia? Media, SabrSessionConfig? SabrConfig)> GetFastStreamUrlAsync(
        string videoId, ResolveAttempt attempt, CancellationToken cancellationToken)
    {
        // Build the client chain (v6 — mirrors yt-dlp 2026.10.26 production
        // defaults). The authenticated clients are only included when
        // cookies OR an OAuth Bearer are available.
        List<InnerTubeClientInfo> clientsToTry = new()
        {
            InnerTubeClients.VisionOS,

            // tv_samsung (clientName TVHTML5, Samsung Tizen smart TV):
            // yt-dlp's #1 default client for EVERYONE as of Oct 2026 — no
            // PO token required, returns direct URLs, supports cookies.
            InnerTubeClients.TvSamsung
        };

        if (HasAuthentication)
        {
            // yt-dlp's authenticated defaults: web_embedded → tv_samsung → web
            clientsToTry.Add(InnerTubeClients.WebEmbedded);
            clientsToTry.Add(InnerTubeClients.Web);
            Trace($"[Resolve] Authentication detected ({(HasCookies ? "cookies" : "oauth")}) — WEB_EMBEDDED_PLAYER + WEB added to fallback chain");
        }

        clientsToTry.Add(InnerTubeClients.Android);

        Trace($"[Resolve] Client chain: {string.Join(" → ", clientsToTry.Select(c => c.Name))}");

        // v7 (SABR): filled when a client answers SABR-only; used as the
        // last resort if the whole chain finds no direct URL.
        SabrSessionConfig? sabrConfig = null;

        foreach (InnerTubeClientInfo client in clientsToTry)
        {
            if (IsStreamClientBlocked(videoId, client.Name))
            {
                Trace($"[Resolve] Skipping {client.Name} — circuit breaker open for {videoId}");
                attempt.Outcome($"{client.Name}: skipped (recent HTTP 403)");
                continue;
            }

            for (int tryIndex = 1; tryIndex <= MaxClientAttempts; tryIndex++)
            {
                try
                {
                    HttpRequestMessage request = await BuildPlayerRequestAsync(videoId, client);
                    request.Content = BuildPlayerPayload(videoId, client);

                    if (!string.IsNullOrEmpty(PoToken) &&
                        (client.Name == "ANDROID" || client.Name.StartsWith("WEB")))
                        attempt.PoTokenAttached = true;

                    HttpResponseMessage response = await _innerTubeClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    using (response)
                    {
                        if ((int)response.StatusCode == 403)
                            MarkStreamClientFailed(videoId, client.Name, 403);

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
                        // SKIPPED while a GVS PO token is set: the token is bound to the
                        // visitorData it was minted for (yt-dlp PO-token guide), so an imported
                        // ***VISITOR DATA*** + ***PO TOKEN*** pair must keep traveling together —
                        // adopting the response's visitorData would unbind the token and the
                        // stream protection attestation would never pass.
                        if (string.IsNullOrEmpty(PoToken) &&
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

                            // SABR detection: formats exist but none carry a direct URL or
                            // signatureCipher — YouTube only offers this video through its
                            // SABR streaming protocol. The response is captured for the
                            // SABR playback fallback (see TrySabrPlaybackAsync).
                            if (audioCount > 0 && withUrl == 0 && withCipher == 0)
                            {
                                bool hasSabrUrl = streamingData.TryGetProperty("serverAbrStreamingUrl", out JsonElement sabrUrlEl);
                                Trace($"[Resolve] {client.Name}: SABR-only response detected (no playable URLs{(hasSabrUrl ? ", serverAbrStreamingUrl present" : "")}) — captured for the SABR fallback");
                                attempt.SabrOnly = true;
                                attempt.Outcome($"{client.Name}: SABR-only — {audioCount} audio formats, none with a direct URL (YouTube's new streaming protocol)");

                                if (hasSabrUrl && sabrUrlEl.GetString() is string sabrUrl && !string.IsNullOrEmpty(sabrUrl))
                                {
                                    SabrSessionConfig? captured = TryBuildSabrConfig(root, formats, client, videoId, sabrUrl);
                                    if (captured != null)
                                        sabrConfig = captured;
                                }
                                break; // try next client
                            }

                            if (withUrl > 0)
                            {
                                // Direct URL — find best audio with a URL
                                string? streamUrl = FindBestAudioWithUrl(formats);
                                if (streamUrl != null)
                                {
                                    Trace($"[Resolve] SUCCESS — client: {client.Name} (direct URL)");
                                    attempt.Outcome($"{client.Name}: OK — direct audio URL");
                                    return (new ResolvedMedia(streamUrl, client.UA), null);
                                }

                                Trace($"[Resolve] {client.Name}: direct URLs present but no usable audio format");
                                attempt.Outcome($"{client.Name}: {withUrl} direct URL(s), none of them audio");
                            }

                            if (withCipher > 0)
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

        return (null, sabrConfig);
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
    private static async Task<ResolvedMedia?> TrySabrPlaybackAsync(
        SabrSessionConfig config, ResolveAttempt attempt, CancellationToken cancellationToken)
    {
        SabrStreamSession? session = null;
        try
        {
            session = SabrStreamRegistry.GetOrCreate(config);
            session.EnsureRunning();

            using CancellationTokenSource preflightCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preflightCts.CancelAfter(TimeSpan.FromSeconds(12));

            long firstBytes = await session.WaitForFirstBytesAsync(1, preflightCts.Token);

            Trace($"[Resolve] SABR session started — itag {session.ActiveItag}, {firstBytes} bytes received");
            attempt.Outcome($"SABR: streaming session started (itag {session.ActiveItag}, {firstBytes} bytes received)");

            return new ResolvedMedia(SabrStreamRegistry.MakeUri(session), DefaultUserAgent);
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
                VisitorData: VisitorData,
                UserAgent: client.UA);

            return new SabrSessionConfig(
                VideoId: videoId,
                ServerAbrStreamingUrl: sabrUrl,
                UstreamerConfigB64: ustreamerConfig,
                ClientInfo: clientInfo,
                PreferredAudio: audio,
                PoToken: PoToken,
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
    //  Private: request building
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<HttpRequestMessage> BuildPlayerRequestAsync(string videoId, InnerTubeClientInfo client)
    {
        string baseUrl = client.Name.Contains("MUSIC") || client.Name == "WEB_REMIX"
            ? "https://music.youtube.com"
            : "https://www.youtube.com";

        string apiUrl = $"{baseUrl}/youtubei/v1/player?key={InnerTubeApiKey}&prettyPrint=false";

        HttpRequestMessage request = new(HttpMethod.Post, apiUrl);
        // WebEmbedded's UA ends in "Safari/605.1.15,gzip(gfe)" — the exact UA
        // yt-dlp ships for that client — and .NET's validated header parser
        // rejects the ",gzip(gfe)" suffix (ProductInfoHeaderValue syntax).
        // TryAddWithoutValidation sends the UA byte-for-byte as yt-dlp does.
        request.Headers.TryAddWithoutValidation("User-Agent", client.UA);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-YouTube-Client-Name", client.Id);
        request.Headers.Add("X-YouTube-Client-Version", client.Version);

        // X-Origin + Referer for WEB-family and VISIONOS clients (yt-dlp sends
        // Origin on every innertube API call, so the new TVHTML5 client gets it too)
        bool needsOrigin = client.Name.StartsWith("WEB") || client.Name == "VISIONOS" || client.Name == "TVHTML5";
        if (needsOrigin)
        {
            request.Headers.Add("X-Origin", baseUrl);
            request.Headers.Add("Referer", $"{baseUrl}/");
        }

        // ── Cookie authentication ────────────────────────────────────────────
        // Cookies are attached to the cookie-supporting clients: the WEB family
        // (WEB, WEB_EMBEDDED_PLAYER) and the Samsung TV client (TVHTML5 —
        // SUPPORTS_COOKIES in yt-dlp's client table). VISIONOS and ANDROID stay
        // anonymous — sending cookies to them confuses YouTube and causes
        // "no streamingData" responses.
        string? cookie = Cookies;
        bool isWebClient = client.Name.StartsWith("WEB");
        bool supportsCookies = isWebClient || client.Name == "TVHTML5";
        bool hasCookiesForThisClient = !string.IsNullOrEmpty(cookie) && supportsCookies;

        // Don't send visitorData with cookie-authenticated requests — stale
        // visitorData causes "The page needs to be reloaded" errors.
        // OAuth requests DO carry visitorData (mirrors ytmusicapi).
        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd) && !hasCookiesForThisClient)
            request.Headers.Add("X-Goog-Visitor-Id", vd);

        // OAuth Bearer (device-flow login) takes priority over cookies when
        // both exist; cookies + SAPISIDHASH remain the fallback.
        if (isWebClient && !hasCookiesForThisClient && OAuthTokenProvider != null)
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

    private static HttpContent BuildPlayerPayload(string videoId, InnerTubeClientInfo client)
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
        // Cookies go to the WEB family + TVHTML5 — VISIONOS and ANDROID are
        // anonymous.
        bool supportsCookies = client.Name.StartsWith("WEB") || client.Name == "TVHTML5";
        bool cookieAuth = !string.IsNullOrEmpty(Cookies) && supportsCookies;

        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd) && !cookieAuth)
            clientObj["visitorData"] = vd;

        // v6 payload upgrades (all mirrored from yt-dlp's player request):
        //   contentCheckOk / racyCheckOk — bypass the content-warning gate
        //     that otherwise makes borderline videos UNPLAYABLE;
        //   playbackContext.contentPlaybackContext.html5Preference — what
        //     every real client sends.
        Dictionary<string, object> payload = new()
        {
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
            ["playbackContext"] = new Dictionary<string, object>
            {
                ["contentPlaybackContext"] = new Dictionary<string, object>
                {
                    ["html5Preference"] = "HTML5_PREF_WANTS"
                }
            },
            ["context"] = new Dictionary<string, object>
            {
                ["client"] = clientObj,
                ["user"] = new Dictionary<string, object>()
            }
        };

        // GVS PO token (bot attestation): attached to the clients whose format
        // URLs are gated behind one (ANDROID + the WEB family, per yt-dlp's
        // PO-token guide). A valid GVS token restores direct URLs on clients
        // that would otherwise return SABR-only or 403. The token is bound to
        // the visitorData sent above, so both travel together.
        string? poToken = PoToken;
        if (!string.IsNullOrEmpty(poToken) &&
            (client.Name == "ANDROID" || client.Name.StartsWith("WEB")))
        {
            payload["serviceIntegrityDimensions"] = new Dictionary<string, object>
            {
                ["poToken"] = poToken
            };
            Trace($"[Resolve] Attached GVS PO token to {client.Name} payload");
        }

        string json = JsonSerializer.Serialize(payload, _jsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: stream selection (codec-aware: opus > mp4a > other)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the best audio format with a direct (non-ciphered) URL.
    /// Enumerates the list once, picks by codec rank → bitrate → sample rate.
    /// </summary>
    private static string? FindBestAudioWithUrl(List<JsonElement> formats)
    {
        JsonElement best = default;
        int bestRank = 0, bestBitrate = 0, bestSampleRate = 0;
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

            if (!found || rank > bestRank ||
                (rank == bestRank && bitrate > bestBitrate) ||
                (rank == bestRank && bitrate == bestBitrate && sampleRate > bestSampleRate))
            {
                best = f;
                bestRank = rank;
                bestBitrate = bitrate;
                bestSampleRate = sampleRate;
                found = true;
            }
        }

        return found && best.TryGetProperty("url", out JsonElement urlProp)
            ? urlProp.GetString()
            : null;
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