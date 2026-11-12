using Musicthingy.Helpers;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
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
// Client chain (Aug 2026):
//   1. VISIONOS 1.02 (anonymous, fast) — works for ~90% of videos
//   2. WEB + cookies (authenticated) — for restricted videos
//   3. ANDROID (anonymous) — last resort, muxed itag 18 only
//   4. YoutubeExplode (with cookies if available) — cipher deciphering fallback
//
// SABR: YouTube's new server-side streaming protocol (proprietary binary,
// replaces DASH/progressive URLs). When a video only returns SABR formats
// (no direct URLs, no signatureCipher), we can't play it with ExoPlayer. This
// is detected and logged. Future SABR support would require a custom ExoPlayer
// DataSource. Client status (per yt-dlp's PO-Token-Guide, mid-2026): WEB is
// SABR-only for most formats; ANDROID/IOS need PO tokens (GVS); PO-token-free
// clients are ANDROID_VR / VISIONOS (same XR family) and web_embedded.
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

    // ── Per-client circuit breaker (10-min cooldown on HTTP 403) ─────────────
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();
    private static readonly TimeSpan FailedClientBackoff = TimeSpan.FromMinutes(10);

    private static void MarkStreamClientFailed(string videoId, string clientKey, int httpStatus)
    {
        if (httpStatus != 403) return;
        _failedUntil[$"{videoId}:{clientKey}"] = DateTime.UtcNow + FailedClientBackoff;
        Debug.WriteLine($"[Resolve] Circuit breaker OPEN for {videoId}:{clientKey}");
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
            Debug.WriteLine($"[Resolve] HasCookies={HasCookies}, trying InnerTube clients for {videoId.Value.Value}");
            resolvedMedia = await GetFastStreamUrlAsync(videoId.Value.Value, cancellationToken);

            // YoutubeExplode fallback — always try, even if InnerTube failed.
            // YoutubeExplode has its own cipher deciphering + watch-page scraping.
            if (resolvedMedia == null)
            {
                Debug.WriteLine("[Resolve] InnerTube chain failed — trying YoutubeExplode fallback");
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
                            Debug.WriteLine("[Resolve] YoutubeExplode SUCCESS");
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Resolve] YoutubeExplode error: {ex.Message}");
                }
            }
        }

        if (resolvedMedia == null)
        {
            Debug.WriteLine($"[Resolve] ALL CLIENTS FAILED for {trackUrl} — returning null");
            return null;
        }

        if (resolvedMedia.Url != trackUrl)
            CacheStreamUrl(trackUrl, resolvedMedia);

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
            ? new[] { InnerTubeClients.VisionOS, InnerTubeClients.Web }
            : new[] { InnerTubeClients.VisionOS };

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

    private async Task<ResolvedMedia?> GetFastStreamUrlAsync(string videoId, CancellationToken cancellationToken)
    {
        // Build the client chain. WEB is only included when authenticated
        // (cookies OR OAuth Bearer).
        List<InnerTubeClientInfo> clientsToTry = new()
        {
            InnerTubeClients.VisionOS
        };

        if (HasAuthentication)
        {
            clientsToTry.Add(InnerTubeClients.Web);
            Debug.WriteLine($"[Resolve] Authentication detected ({(HasCookies ? "cookies" : "oauth")}) — WEB client added to fallback chain");
        }

        clientsToTry.Add(InnerTubeClients.Android);

        Debug.WriteLine($"[Resolve] Client chain: {string.Join(" → ", clientsToTry.Select(c => c.Name))}");

        foreach (InnerTubeClientInfo client in clientsToTry)
        {
            if (IsStreamClientBlocked(videoId, client.Name))
            {
                Debug.WriteLine($"[Resolve] Skipping {client.Name} — circuit breaker open for {videoId}");
                continue;
            }

            for (int attempt = 1; attempt <= MaxClientAttempts; attempt++)
            {
                try
                {
                    HttpRequestMessage request = await BuildPlayerRequestAsync(videoId, client);
                    request.Content = BuildPlayerPayload(videoId, client);

                    HttpResponseMessage response = await _innerTubeClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    using (response)
                    {
                        if ((int)response.StatusCode == 403)
                            MarkStreamClientFailed(videoId, client.Name, 403);

                        string json = await response.Content.ReadAsStringAsync(cancellationToken);

                        // Keep rotating session cookies (__Secure-*PSIDTS) fresh so
                        // cookie sessions captured via web login do not expire.
                        HarvestSessionCookies(response);

                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        // Update visitorData from response (for non-cookie-authenticated requests)
                        if (root.TryGetProperty("responseContext", out JsonElement rc) &&
                            rc.TryGetProperty("visitorData", out JsonElement vd))
                        {
                            string? newVd = vd.GetString();
                            if (!string.IsNullOrEmpty(newVd) && newVd != VisitorData)
                            {
                                VisitorData = newVd;
                                Debug.WriteLine("[Resolve] Updated visitorData from player response");
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
                                Debug.WriteLine($"[Resolve] {client.Name} returned {statusStr} for {videoId}: {reason ?? "no reason"}");
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

                            Debug.WriteLine($"[Resolve] {client.Name}: {formats.Count} formats, {audioCount} audio, {withUrl} direct, {withCipher} ciphered");

                            // SABR detection: if there are formats but none have url or signatureCipher,
                            // they're SABR streams (YouTube's new protocol) which ExoPlayer can't play.
                            if (audioCount > 0 && withUrl == 0 && withCipher == 0)
                            {
                                Debug.WriteLine($"[Resolve] {client.Name}: SABR-only response detected (no playable URLs) — ExoPlayer cannot play SABR streams");
                                break; // try next client
                            }

                            if (withUrl > 0)
                            {
                                // Direct URL — find best audio with a URL
                                string? streamUrl = FindBestAudioWithUrl(formats);
                                if (streamUrl != null)
                                {
                                    Debug.WriteLine($"[Resolve] SUCCESS — client: {client.Name} (direct URL)");
                                    return new ResolvedMedia(streamUrl, client.UA);
                                }
                            }

                            if (withCipher > 0)
                            {
                                Debug.WriteLine($"[Resolve] {client.Name}: ciphered URLs only — will use YoutubeExplode fallback");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"[Resolve] {client.Name}: no streamingData in response");
                        }

                        break; // no usable streaming data, try next client
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (IOException ex)
                {
                    Debug.WriteLine($"[Resolve] {client.Name} attempt {attempt} IOException: {ex.Message}");
                    if (attempt < MaxClientAttempts)
                        await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Resolve] {client.Name} attempt {attempt} error: {ex.Message}");
                    break;
                }
            }
        }

        return null;
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
        request.Headers.Add("User-Agent", client.UA);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-YouTube-Client-Name", client.Id);
        request.Headers.Add("X-YouTube-Client-Version", client.Version);

        // X-Origin + Referer for WEB and VISIONOS clients
        bool needsOrigin = client.Name.StartsWith("WEB") || client.Name == "VISIONOS";
        if (needsOrigin)
        {
            request.Headers.Add("X-Origin", baseUrl);
            request.Headers.Add("Referer", $"{baseUrl}/");
        }

        // ── Cookie authentication ────────────────────────────────────────────
        // Cookies are ONLY attached to the WEB client. VISIONOS and ANDROID are
        // anonymous clients — sending cookies to them confuses YouTube and causes
        // "no streamingData" responses.
        string? cookie = Cookies;
        bool isWebClient = client.Name.StartsWith("WEB");
        bool hasCookiesForThisClient = !string.IsNullOrEmpty(cookie) && isWebClient;

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
        // Cookies are only for WEB — VISIONOS and ANDROID are anonymous.
        bool needsCookies = client.Name.StartsWith("WEB");
        bool cookieAuth = !string.IsNullOrEmpty(Cookies) && needsCookies;

        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd) && !cookieAuth)
            clientObj["visitorData"] = vd;

        object defaultPayload = new
        {
            videoId,
            context = new { client = clientObj, user = new { } }
        };
        string defaultJson = JsonSerializer.Serialize(defaultPayload, _jsonOptions);
        return new StringContent(defaultJson, Encoding.UTF8, "application/json");
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

        // NOTE: TVHTML5_SIMPLY_EMBEDDED_PLAYER was removed — it returns
        // "YouTube is no longer supported in this application or device."
        // as of Aug 2026. Do not re-add it.

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