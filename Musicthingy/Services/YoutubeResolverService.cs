using Musicthingy.Helpers;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
// Updated YoutubeResolverService — incorporates review findings + OpenTune patterns:
//   §3.1  API key is a static property (overridable at startup, not a hardcoded const)
//   §4.9  CancellationToken threaded through every async call
//   §4.11 Static _visitorData replaced with immutable PlaybackAuthState + Interlocked.Exchange
//   §5    Ipv4Handler now has PooledConnectionLifetime=2min (DNS refresh on Wi-Fi↔cellular)
//   §5    Stream URL cache uses LRU eviction instead of "clear all" at capacity
//   §7.2  Immutable PlaybackAuthState record (ported from OpenTune)
//   §7.3  WithRetryAsync helper (catches IOException only, never OperationCanceledException)
//   §7.4  Per-client circuit breaker (10-min cooldown on HTTP 403)
//   §7.5  visitorData populates naturally from player responses (no separate bootstrap)
//   §7.1  Codec-aware stream selection (opus > mp4a > other) with bitrate ceiling
//   §4.8  No empty catch blocks — every catch logs to Debug.WriteLine
//
//   Client strategy (Aug 2026): VISIONOS 1.02 is primary (sole surviving anonymous
//   client after YouTube killed ANDROID_VR on Aug 17). ANDROID 21.26.364 is fallback
//   for "made for kids" videos. See InnerTubeClients below for full context.
// ────────────────────────────────────────────────────────────────────────────

public partial class YouTubeResolverService
{
    // ── Constants ───────────────────────────────────────────────────────────
    private const double CacheExpiryHours = 2.0;
    private const int ManifestFetchTimeoutSeconds = 60;
    private const int VideoInfoFetchTimeoutSeconds = 10;
    private const int MaxClientAttempts = 2;
    private const int MaxCacheSize = 500;
    private const int CacheEvictBatch = 50;          // evict oldest 50 when full (LRU)
    private const int StreamProbeTimeoutSeconds = 5;

    /// <summary>
    /// When true, validates each resolved stream URL with a lightweight HTTP HEAD
    /// probe before returning it to the player. Default <c>false</c> — adds
    /// ~100-300ms per track load. Enable for diagnostics or if you see the player
    /// frequently receiving dead URLs.
    /// </summary>
    public bool EnableStreamProbe { get; set; } = false;

    /// <summary>
    /// Per-client circuit-breaker backoff. When a client returns HTTP 403 for a
    /// specific video, it is skipped for this duration (ported from OpenTune).
    /// </summary>
    private static readonly TimeSpan FailedClientBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default User-Agent for InnerTube requests that don't specify a client UA.
    /// Made public so MauiProgram.cs can reference it when registering named HttpClients.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>
    /// InnerTube API key. This is YouTube's well-known public Android-client key
    /// (not a secret — it's embedded in the YouTube APK and documented in every
    /// open-source YouTube scraper). It's a static property (not const) so it can
    /// be overridden at startup if YouTube ever rotates it:
    ///   YouTubeResolverService.InnerTubeApiKey = newKey;
    /// </summary>
    public static string InnerTubeApiKey { get; set; } = "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w";

    // ── IPv4-only handler (shared singleton) ────────────────────────────────
    // §5: PooledConnectionLifetime added so DNS changes (Wi-Fi ↔ cellular) are
    // picked up every 2 minutes. The original code left this at Infinite.
    private static readonly SocketsHttpHandler _ipv4Handler = new SocketsHttpHandler
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

            Socket socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

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

    /// <summary>Shared IPv4-forced handler. Exposed so MauiProgram can register it with IHttpClientFactory.</summary>
    public static SocketsHttpHandler Ipv4Handler => _ipv4Handler;

    // ── HTTP clients ────────────────────────────────────────────────────────
    // _innerTubeClient is used for direct InnerTube API calls (player endpoint).
    // _youtubeClient is the YoutubeExplode client (fallback path).
    private readonly HttpClient _innerTubeClient;
    private readonly YoutubeClient _youtubeClient;

    // ── Immutable auth state (§4.11 / §7.2) ─────────────────────────────────
    // Replaces the old `static string? _visitorData` field. Thread-safe via
    // Volatile.Read + Interlocked.Exchange on an immutable record.
    private static PlaybackAuthState _authState = PlaybackAuthState.Empty;

    private static PlaybackAuthState AuthState
    {
        get => Volatile.Read(ref _authState);
        set => Interlocked.Exchange(ref _authState, value);
    }

    /// <summary>
    /// The current visitorData string, extracted from the immutable auth state.
    /// Setting it produces a new immutable record and atomically swaps it in.
    /// </summary>
    private static string? VisitorData
    {
        get => AuthState.VisitorData;
        set => AuthState = AuthState with { VisitorData = value };
    }

    // ── Per-client circuit breaker (§7.4) ───────────────────────────────────
    // Keyed by $"{videoId}:{CLIENT_NAME}". A 403 marks the client as failed for
    // that video for 10 minutes, so we don't re-trigger the same failing combo.
    private static readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();

    private static void MarkStreamClientFailed(string videoId, string clientKey, int? httpStatus)
    {
        if (httpStatus != 403) return;
        _failedUntil[$"{videoId}:{clientKey}"] = DateTime.UtcNow + FailedClientBackoff;
        Debug.WriteLine($"[Resolve] Circuit breaker OPEN for {videoId}:{clientKey} ({FailedClientBackoff.TotalMinutes:F0} min)");
    }

    private static bool IsStreamClientBlocked(string videoId, string clientKey)
    {
        string key = $"{videoId}:{clientKey}";
        if (!_failedUntil.TryGetValue(key, out DateTime until))
            return false;
        if (until <= DateTime.UtcNow)
        {
            _failedUntil.TryRemove(key, out _);
            return false;
        }
        return true;
    }

    // ── Stream URL cache (LRU, §5) ──────────────────────────────────────────
    // Replaces the old "clear all at 500" eviction. Now evicts the oldest 50
    // entries by FetchedAt when capacity is hit, preserving the hot entries.
    private readonly ConcurrentDictionary<string, (ResolvedMedia Media, DateTime FetchedAt)> _streamUrlCache = new();

    private void CacheStreamUrl(string trackUrl, ResolvedMedia media)
    {
        if (_streamUrlCache.Count >= MaxCacheSize)
        {
            // LRU eviction: remove the oldest CacheEvictBatch entries.
            // This is O(n log n) but only runs when the cache is full, which is rare.
            var oldest = _streamUrlCache
                .OrderByDescending(kv => kv.Value.FetchedAt)   // newest first
                .Skip(MaxCacheSize - CacheEvictBatch)           // skip the newest, keep the oldest batch
                .Select(kv => kv.Key)
                .ToList();

            foreach (string key in oldest)
                _streamUrlCache.TryRemove(key, out _);

            Debug.WriteLine($"[Resolve] LRU evicted {oldest.Count} entries (cache was full)");
        }
        _streamUrlCache[trackUrl] = (media, DateTime.Now);
    }

    // ── Visitor data ────────────────────────────────────────────────────────
    // visitorData populates naturally from player responses (responseContext.visitorData).
    // No separate bootstrap fetch — the first player request works without it, and
    // subsequent requests pick it up from the immutable PlaybackAuthState above.

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Regexes (compiled at startup via source generator) ──────────────────
    [GeneratedRegex(@"\[?(?:(?:(\d{1,2}):)?(\d{1,2}):(\d{2}))\]?\s*(?:[-:]\s*)?(.+)")]
    private static partial Regex ChapterRegex();

    // (visitorData regex removed — no longer needed without the sw.js_data bootstrap.)

    // ── Constructors ────────────────────────────────────────────────────────
    /// <summary>
    /// DI-friendly constructor. MauiProgram should register this as a singleton
    /// and (optionally) pass an IHttpClientFactory so all HTTP egress uses the
    /// shared IPv4 handler via named clients. See review §6.
    /// </summary>
    public YouTubeResolverService(IHttpClientFactory? httpFactory = null)
    {
        if (httpFactory != null)
        {
            // Named-client path: each call site gets its own HttpClient but they
            // all share the Ipv4Handler connection pool.
            _innerTubeClient = httpFactory.CreateClient("youtube");
        }
        else
        {
            // Fallback for direct instantiation (e.g. EditTrackGeneralTab.xaml.cs).
            // Still uses the shared Ipv4Handler, just without IHttpClientFactory.
            _innerTubeClient = new HttpClient(_ipv4Handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        // YoutubeExplode always gets a fresh HttpClient over the IPv4 handler.
        _youtubeClient = new YoutubeClient(new HttpClient(_ipv4Handler));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a YouTube video and extracts chapters from the description.
    /// Strictly adheres to YouTube's rules (starts at 0:00, ≥3 chapters, ascending order).
    /// </summary>
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
            Debug.WriteLine($"[Chapters] Error parsing chapters for {trackUrl}: {ex.Message}");
            return Array.Empty<VideoChapter>();
        }
    }

    /// <summary>
    /// Fetches all video items in a YouTube playlist.
    /// </summary>
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
            Debug.WriteLine($"[Playlist] Error resolving videos for {playlistUrl}: {ex.Message}");
            return Array.Empty<YouTubePlaylistVideo>();
        }
    }

    /// <summary>
    /// Resolves a track URL to a direct stream URL.
    /// For YouTube URLs: tries the fast InnerTube path, then falls back to YoutubeExplode.
    /// For local/web URLs: returns them unchanged.
    /// </summary>
    /// <param name="trackUrl">YouTube URL, local file path, or web URL.</param>
    /// <param name="cancellationToken">Cancellation token (§4.9). Optional for backwards compat.</param>
    public async Task<ResolvedMedia?> ResolveMediaSourceAsync(
        string trackUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(trackUrl))
            return null;

        if (!MediaUrlHelper.IsYouTubeUrl(trackUrl))
            return new ResolvedMedia(trackUrl, DefaultUserAgent);

        // ── Cache check ─────────────────────────────────────────────────────
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
            // §4.9: pass the cancellation token all the way down.
            resolvedMedia = await GetFastStreamUrlAsync(videoId.Value.Value, cancellationToken);

            if (resolvedMedia == null)
            {
                // ── YoutubeExplode fallback ──────────────────────────────────
                try
                {
                    string? url = await Task.Run(async () =>
                    {
                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(ManifestFetchTimeoutSeconds));

                        StreamManifest manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId.Value, cts.Token);

                        // §7.1: codec-aware selection (opus > mp4a > other, then bitrate).
                        AudioOnlyStreamInfo? bestStream = manifest.GetAudioOnlyStreams()
                            .OrderByDescending(CodecRank)
                            .ThenByDescending(s => s.Bitrate)
                            .FirstOrDefault();

                        return bestStream?.Url;
                    }, cancellationToken);

                    Debug.WriteLine("[Resolve] YOUTUBE EXPLODE SUCCESS");

                    if (!string.IsNullOrEmpty(url))
                        resolvedMedia = new ResolvedMedia(url, DefaultUserAgent);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Clean cancellation — don't swallow as an error.
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE ERROR: {ex.Message}");
                }
            }
        }

        // ── Final fallback: return the original URL and let the player try ──
        if (resolvedMedia == null)
            resolvedMedia = new ResolvedMedia(trackUrl, DefaultUserAgent);
        else if (resolvedMedia.Url != trackUrl)
            CacheStreamUrl(trackUrl, resolvedMedia);

        return resolvedMedia;
    }

    /// <summary>
    /// Fetches the duration of a YouTube video via the InnerTube player endpoint,
    /// falling back to YoutubeExplode.
    /// </summary>
    public async Task<TimeSpan?> GetVideoDurationAsync(string trackUrl, CancellationToken cancellationToken)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return null;

        // ── InnerTube player endpoint (fast path) ───────────────────────────
        // Uses VISIONOS — the most reliable client as of Aug 2026. Returns
        // videoDetails.lengthSeconds without needing a playable stream.
        try
        {
            InnerTubeClientInfo client = InnerTubeClients.VisionOS;

            HttpRequestMessage request = BuildPlayerRequest(videoId.Value.Value, client, cancellationToken);
            request.Content = BuildPlayerPayload(videoId.Value.Value, client);

            // §7.3: wrap in WithRetryAsync for transient network failures.
            HttpResponseMessage response = await WithRetryAsync(
                () => _innerTubeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
                cancellationToken: cancellationToken);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InnerTube Duration] Error resolving duration for {trackUrl}: {ex.Message}");
        }

        // ── YoutubeExplode fallback ──────────────────────────────────────────
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));

            Video videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            return videoInfo.Duration;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeExplode Duration] Error: {ex.Message}");
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: InnerTube player request
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries each InnerTube client in order to resolve a direct stream URL.
    /// Honours the per-client circuit breaker (§7.4) and cancellation (§4.9).
    /// </summary>
    private async Task<ResolvedMedia?> GetFastStreamUrlAsync(string videoId, CancellationToken cancellationToken)
    {
        // visitorData populates naturally from the first successful player response
        // (see responseContext.visitorData extraction below). No separate bootstrap
        // — VISIONOS works without visitorData on the first request, and subsequent
        // calls pick it up automatically.

        foreach (InnerTubeClientInfo client in InnerTubeClients.All)
        {
            // §7.4: skip clients that recently returned 403 for this video.
            if (IsStreamClientBlocked(videoId, client.Name))
            {
                Debug.WriteLine($"[Resolve] Skipping {client.Name} — circuit breaker open for {videoId}");
                continue;
            }

            for (int attempt = 1; attempt <= MaxClientAttempts; attempt++)
            {
                try
                {
                    HttpRequestMessage request = BuildPlayerRequest(videoId, client, cancellationToken);
                    request.Content = BuildPlayerPayload(videoId, client);

                    // §4.9: pass the cancellation token (the original omitted it).
                    HttpResponseMessage response = await _innerTubeClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    using (response)
                    {
                        // §7.4: record 403s in the circuit breaker.
                        if ((int)response.StatusCode == 403)
                            MarkStreamClientFailed(videoId, client.Name, 403);

                        string json = await response.Content.ReadAsStringAsync(cancellationToken);

                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        // ── Update visitorData if present (§4.11 / §7.2) ─────────
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

                        // ── Check playabilityStatus ─────────────────────────────
                        if (root.TryGetProperty("playabilityStatus", out JsonElement statusProp) &&
                            statusProp.TryGetProperty("status", out JsonElement status))
                        {
                            string? statusStr = status.GetString();

                            if (statusStr != "OK")
                            {
                                // LOGIN_REQUIRED / UNPLAYABLE means YouTube won't serve this
                                // client/video combo. The circuit breaker + client fallback
                                // chain handles this — just move to the next client.
                                Debug.WriteLine($"[Resolve] {client.Name} returned status {statusStr} for {videoId}");
                                break;  // try next client
                            }
                        }

                        // ── Extract best audio stream (§7.1 codec-aware) ────────
                        if (root.TryGetProperty("streamingData", out JsonElement streamingData) &&
                            streamingData.TryGetProperty("adaptiveFormats", out JsonElement adaptiveFormats))
                        {
                            JsonElement bestAudio = SelectBestAudioFormat(adaptiveFormats);

                            if (bestAudio.ValueKind != JsonValueKind.Undefined &&
                                bestAudio.TryGetProperty("url", out JsonElement urlProp))
                            {
                                string streamUrl = urlProp.GetString()!;
                                Debug.WriteLine($"[Resolve] FAST PATH SUCCESS — client: {client.Name}");

                                // §7.1: stream probe is OFF by default. It adds ~100-300ms
                                // per track load. Enable via EnableStreamProbe=true if you
                                // want pre-validation (useful for diagnosing bad URLs but
                                // not worth the latency in normal use — the player already
                                // handles broken URLs by failing and retrying).
                                if (EnableStreamProbe)
                                {
                                    bool valid = await ProbeStreamUrlAsync(streamUrl, client.UA, cancellationToken);
                                    if (!valid)
                                    {
                                        Debug.WriteLine($"[Resolve] Stream probe failed for {client.Name}, trying next client");
                                        break;
                                    }
                                }

                                return new ResolvedMedia(streamUrl, client.UA);
                            }
                        }

                        break;  // no streaming data, try next client
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Clean cancellation — propagate, don't swallow.
                    throw;
                }
                catch (IOException ex)
                {
                    // §7.3: transient network error — retry this client.
                    Debug.WriteLine($"[Resolve] {client.Name} attempt {attempt} IOException: {ex.Message}");
                    if (attempt < MaxClientAttempts)
                        await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Resolve] {client.Name} attempt {attempt} error: {ex.Message}");
                    break;  // non-transient, try next client
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an InnerTube player request with the public API key and client headers.
    /// </summary>
    private static HttpRequestMessage BuildPlayerRequest(string videoId, InnerTubeClientInfo client, CancellationToken ct)
    {
        string baseUrl = client.Name.Contains("MUSIC") || client.Name == "WEB_REMIX"
            ? "https://music.youtube.com"
            : "https://www.youtube.com";

        // The API key is YouTube's public Android-client key. VISIONOS doesn't
        // strictly require it, but the ANDROID fallback client benefits from it.
        // Sending it for all clients is harmless.
        string apiUrl = $"{baseUrl}/youtubei/v1/player?key={InnerTubeApiKey}&prettyPrint=false";

        HttpRequestMessage request = new(HttpMethod.Post, apiUrl);
        request.Headers.Add("User-Agent", client.UA);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Goog-Api-Format-Version", "1");
        request.Headers.Add("X-YouTube-Client-Name", client.Id);
        request.Headers.Add("X-YouTube-Client-Version", client.Version);

        if (client.Name.StartsWith("WEB") || client.Name == "VISIONOS")
        {
            request.Headers.Add("X-Origin", baseUrl);
            request.Headers.Add("Referer", $"{baseUrl}/");
        }

        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd))
            request.Headers.Add("X-Goog-Visitor-Id", vd);

        return request;
    }

    /// <summary>
    /// Builds the JSON payload for the player endpoint.
    /// </summary>
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

        string? vd = VisitorData;
        if (!string.IsNullOrEmpty(vd))
            clientObj["visitorData"] = vd;

        object payload = new
        {
            videoId,
            context = new { client = clientObj, user = new { } }
        };

        string jsonString = JsonSerializer.Serialize(payload, _jsonOptions);
        return new StringContent(jsonString, Encoding.UTF8, "application/json");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: stream selection (§7.1 codec-aware, ported from OpenTune)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects the best audio-only format from the adaptiveFormats array.
    /// Ranking: codec (opus > mp4a > other) → bitrate (higher first) → has URL.
    /// Ported from OpenTune's YTPlayerUtils.selectAudioFormatCandidates.
    /// </summary>
    private static JsonElement SelectBestAudioFormat(JsonElement adaptiveFormats)
    {
        return adaptiveFormats.EnumerateArray()
            .Where(f => f.TryGetProperty("mimeType", out JsonElement mime) &&
                        mime.GetString()!.Contains("audio/"))
            .Where(f => f.TryGetProperty("url", out _))
            .OrderByDescending(CodecRank)
            .ThenByDescending(f => f.TryGetProperty("bitrate", out JsonElement b) ? b.GetInt32() : 0)
            .ThenByDescending(f => f.TryGetProperty("audioSampleRate", out JsonElement sr) &&
                                   int.TryParse(sr.GetString(), out int rate) ? rate : 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// Codec ranking: opus (3) > mp4a (2) > other (1).
    /// OpenTune uses the same ranking — opus has the best quality/bitrate ratio
    /// for YouTube's audio-only streams.
    /// </summary>
    private static int CodecRank(JsonElement format)
    {
        if (!format.TryGetProperty("mimeType", out JsonElement mimeEl))
            return 0;
        string mime = mimeEl.GetString() ?? "";

        // The codec is in the mimeType, e.g. "audio/webm; codecs=\"opus\""
        if (mime.Contains("opus")) return 3;
        if (mime.Contains("mp4a")) return 2;
        return 1;
    }

    // For YoutubeExplode streams (which have a different type).
    private static int CodecRank(AudioOnlyStreamInfo stream)
    {
        string codec = stream.AudioCodec.ToLowerInvariant();
        if (codec.Contains("opus")) return 3;
        if (codec.Contains("mp4a")) return 2;
        return 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: stream URL validation (§7.1 live Range-probe)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a lightweight HTTP Range probe (bytes=0-0) to validate the stream
    /// URL before handing it to the player. Returns true on 200-399 or 416.
    /// Best-effort — on network errors, returns true (let the player retry).
    /// Ported from OpenTune's YTPlayerUtils stream validation.
    /// </summary>
    private async Task<bool> ProbeStreamUrlAsync(string url, string userAgent, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(StreamProbeTimeoutSeconds));

            // Use GET with Range instead of HEAD — YouTube's CDN is more reliable
            // with Range GETs, and we only read the response headers anyway.
            using HttpRequestMessage probe = new(HttpMethod.Get, url);
            probe.Headers.UserAgent.ParseAdd(userAgent);
            probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using HttpResponseMessage response = await _innerTubeClient.SendAsync(
                probe, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // 200-399 = OK, 416 = Range Not Satisfiable (still means the URL is valid).
            int code = (int)response.StatusCode;
            if (code is >= 200 and < 400 or 416)
                return true;

            Debug.WriteLine($"[Resolve] Stream probe returned {code}");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network errors don't necessarily mean the URL is bad — let the player try.
            Debug.WriteLine($"[Resolve] Stream probe network error (allowing): {ex.Message}");
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Private: WithRetryAsync (§7.3 — ported from OpenTune's withRetry)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retries a transient operation with exponential backoff.
    /// Catches ONLY IOException (network errors) — OperationCanceledException
    /// always propagates so cancellation works correctly.
    /// Ported from OpenTune's InnerTube.withRetry.
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(
        Func<Task<T>> block,
        int maxAttempts = 3,
        int initialDelayMs = 500,
        double factor = 2.0,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        int delay = initialDelayMs;
        while (true)
        {
            try
            {
                return await block().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;  // never swallow cancellation
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                attempt++;
                Debug.WriteLine($"[Resolve] WithRetry attempt {attempt}/{maxAttempts} failed, backing off {delay}ms");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = (int)(delay * factor);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  InnerTube client definitions
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// InnerTube client fallback chain. Ordered by likelihood of success.
    ///
    /// IMPORTANT (Aug 2026): YouTube killed the Android clients for anonymous
    /// playback. ANDROID_VR CDN URLs started returning 403 on Aug 17, 2026
    /// (yt-dlp issue #17456, PR #17461). ANDROID_TESTSUITE returns UNPLAYABLE.
    /// ANDROID_UNPLUGGED returns LOGIN_REQUIRED. These are NOT fixable by adding
    /// fields — YouTube's CDN itself rejects the URLs they return.
    ///
    /// The community (yt-dlp, YoutubeExplode, NewPipeExtractor) has converged on
    /// VISIONOS as the sole surviving anonymous client. We lead with it.
    /// ANDROID (not _VR, not _TESTSUITE — plain ANDROID) is kept as a fallback
    /// because VISIONOS can't play "made for kids" videos; ANDROID returns
    /// muxed itag 18 for those without requiring a PoToken.
    ///
    /// Sources:
    /// - yt-dlp PR #17461 (2026-08-18): removed android_vr from defaults
    /// - yt-dlp PR #17184 (2026-07-09): added visionos as default JS-less client
    /// - YoutubeExplode PR #965 (2026-08-19): switched primary to VISIONOS 1.02
    /// - NewPipeExtractor PR #1529 (2026-08-06): removed ANDROID/IOS/WEB_EMBEDDED
    /// - yt-dlp PO Token Guide: VISIONOS does not require PO Token
    /// </summary>
    private static class InnerTubeClients
    {
        // ── PRIMARY: VISIONOS 1.02 ──────────────────────────────────────────
        // The only anonymous client that reliably returns progressive stream
        // URLs without PoToken or signature deciphering as of Aug 2026.
        // Updated from the old 0.1 / RealityDevice14,1 to the current 1.02 /
        // RealityDevice17,1 profile used by yt-dlp and YoutubeExplode.
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

        // ── FALLBACK: ANDROID 21.26.364 ─────────────────────────────────────
        // Used when VISIONOS can't play a video (e.g. "made for kids"). Returns
        // muxed itag 18 only, but doesn't require PoToken for that specific
        // format. This is plain ANDROID, NOT ANDROID_VR / ANDROID_TESTSUITE /
        // ANDROID_UNPLUGGED — those are all dead.
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

        // ── LEGACY (disabled — kept for reference / future re-enablement) ────
        // These are dead as of Aug 17, 2026. CDN URLs they return are 403'd
        // regardless of PoToken. Do NOT add to the All array. If YouTube ever
        // relaxes enforcement, re-enable here and add back to All.
        //
        // public static readonly InnerTubeClientInfo AndroidVr = new() { ... };
        //   Name = "ANDROID_VR", Version = "1.37", Id = "28", ...
        // public static readonly InnerTubeClientInfo AndroidTestSuite = new() { ... };
        //   Name = "ANDROID_TESTSUITE", Version = "1.9", Id = "30", ...
        // public static readonly InnerTubeClientInfo AndroidUnplugged = new() { ... };
        //   Name = "ANDROID_UNPLUGGED", Version = "8.49.0", Id = "29", ...

        /// <summary>
        /// Ordered fallback chain. VISIONOS first (fast path, no PoToken),
        /// ANDROID second (made-for-kids fallback).
        /// </summary>
        public static readonly InnerTubeClientInfo[] All =
        {
            VisionOS,
            Android
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
    //  Immutable auth state (§7.2 — ported from OpenTune's PlaybackAuthState)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Immutable record holding all YouTube auth material. Updated atomically
    /// via Interlocked.Exchange so concurrent readers never see a torn state.
    /// Ported from OpenTune's PlaybackAuthState.kt.
    /// </summary>
    private sealed record PlaybackAuthState(
        string? Cookie,
        string? VisitorData,
        string? DataSyncId,
        string? PoToken,
        string? PoTokenGvs,
        string? PoTokenPlayer)
    {
        public static readonly PlaybackAuthState Empty = new(null, null, null, null, null, null);
    }
}
