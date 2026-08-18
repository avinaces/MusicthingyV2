using Musicthingy.Helpers;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.Services;

// Must be partial to support [GeneratedRegex]
public partial class YouTubeResolverService
{
    private const double CacheExpiryHours = 2.0;
    private const int ManifestFetchTimeoutSeconds = 60;
    private const int VideoInfoFetchTimeoutSeconds = 10;
    private const int MaxClientAttempts = 2;
    private const int PreferredAudioBitrateScore = 1000000;
    private const int MaxCacheSize = 500;

    private const string InnerTubeApiKey = "AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w";
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly YoutubeClient _youtubeClient;
    private readonly ConcurrentDictionary<string, (ResolvedMedia Media, DateTime FetchedAt)> _streamUrlCache = new();

    private static readonly SocketsHttpHandler _ipv4Handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
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

    private static readonly HttpClient _innerTubeClient = new HttpClient(_ipv4Handler);

    public static SocketsHttpHandler Ipv4Handler => _ipv4Handler;

    private static string? _visitorData = null;
    private static readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Matches timestamps like "0:00", "01:23", "1:02:30" and captures the title
    [GeneratedRegex(@"\[?(?:(?:(\d{1,2}):)?(\d{1,2}):(\d{2}))\]?\s*(?:[-:]\s*)?(.+)")]
    private static partial Regex ChapterRegex();

    public YouTubeResolverService()
    {
        _youtubeClient = new YoutubeClient(new HttpClient(_ipv4Handler));
    }

    /// <summary>
    /// Fetches a YouTube video and extracts chapters from the description.
    /// Strictly adheres to YouTube's rules (starts at 0:00, >= 3 chapters, ascending order).
    /// </summary>
    public async Task<IReadOnlyList<VideoChapter>> GetVideoChaptersAsync(string trackUrl)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return Array.Empty<VideoChapter>();

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));
            Video video = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            string description = video.Description;

            if (string.IsNullOrWhiteSpace(description))
            {
                return Array.Empty<VideoChapter>();
            }

            MatchCollection matches = ChapterRegex().Matches(description);
            List<VideoChapter> validChapters = new();
            bool hasFoundStart = false;

            foreach (Match match in matches)
            {
                int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
                int minutes = int.Parse(match.Groups[2].Value);
                int seconds = int.Parse(match.Groups[3].Value);

                TimeSpan timestamp = new TimeSpan(hours, minutes, seconds);
                string title = match.Groups[4].Value.Trim();

                if (!hasFoundStart)
                {
                    if (timestamp == TimeSpan.Zero)
                    {
                        hasFoundStart = true;
                        validChapters.Add(new VideoChapter(timestamp, title));
                    }
                }
                else
                {
                    if (timestamp > validChapters[^1].Timestamp)
                    {
                        validChapters.Add(new VideoChapter(timestamp, title));
                    }
                }
            }

            if (validChapters.Count >= 3)
            {
                return validChapters;
            }

            return Array.Empty<VideoChapter>();
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
    public async Task<IReadOnlyList<YouTubePlaylistVideo>> GetPlaylistVideosAsync(string playlistUrl, CancellationToken cancellationToken = default)
    {
        PlaylistId? playlistId = PlaylistId.TryParse(playlistUrl);
        if (playlistId == null) return Array.Empty<YouTubePlaylistVideo>();

        try
        {
            List<YouTubePlaylistVideo> videos = new();

            await foreach (PlaylistVideo video in _youtubeClient.Playlists.GetVideosAsync(playlistId.Value, cancellationToken))
            {
                // Always use mqdefault (16:9) to prevent YouTube's 4:3 black bars
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

    public async Task<ResolvedMedia?> ResolveMediaSourceAsync(string trackUrl)
    {
        if (string.IsNullOrEmpty(trackUrl))
            return null;

        if (MediaUrlHelper.IsYouTubeUrl(trackUrl))
        {
            if (_streamUrlCache.TryGetValue(trackUrl, out (ResolvedMedia Media, DateTime FetchedAt) cached)
                && (DateTime.Now - cached.FetchedAt).TotalHours < CacheExpiryHours)
            {
                Debug.WriteLine($"[Resolve] CACHE HIT");
                return cached.Media;
            }

            ResolvedMedia? resolvedMedia = null;
            VideoId? videoId = VideoId.TryParse(trackUrl);

            if (videoId != null)
            {
                resolvedMedia = await GetFastStreamUrlAsync(videoId.Value.Value);

                if (resolvedMedia == null)
                {
                    try
                    {
                        string? url = await Task.Run(async () =>
                        {
                            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(ManifestFetchTimeoutSeconds));

                            StreamManifest manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId.Value, cts.Token);
                            IEnumerable<AudioOnlyStreamInfo> audioStreams = manifest.GetAudioOnlyStreams();

                            AudioOnlyStreamInfo? bestStream = audioStreams
                                .OrderByDescending(s => s.Container.Name == "webm" ? 1 : 0)
                                .ThenByDescending(s => s.Bitrate)
                                .FirstOrDefault();

                            return bestStream?.Url;
                        });

                        Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE SUCCESS");

                        if (!string.IsNullOrEmpty(url))
                            resolvedMedia = new ResolvedMedia(url, DefaultUserAgent);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE ERROR: {ex.Message}");
                    }
                }
            }

            if (resolvedMedia == null)
            {
                resolvedMedia = new ResolvedMedia(trackUrl, DefaultUserAgent);
            }
            else if (resolvedMedia.Url != trackUrl)
            {
                if (_streamUrlCache.Count >= MaxCacheSize) _streamUrlCache.Clear();
                _streamUrlCache[trackUrl] = (resolvedMedia, DateTime.Now);
            }

            return resolvedMedia;
        }

        return new ResolvedMedia(trackUrl, DefaultUserAgent);
    }

    public async Task<TimeSpan?> GetVideoDurationAsync(string trackUrl, CancellationToken cancellationToken)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);
        if (videoId == null) return null;

        // Try InnerTube API to instantly grab duration without scraping logic
        try
        {
            string apiUrl = $"https://www.youtube.com/youtubei/v1/player?key={InnerTubeApiKey}&prettyPrint=false";
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, apiUrl);

            request.Headers.Add("User-Agent", "com.google.android.youtube/1.9 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002) gzip");
            request.Headers.Add("X-YouTube-Client-Name", "30"); // ANDROID_TESTSUITE
            request.Headers.Add("X-YouTube-Client-Version", "1.9");

            object payload = new
            {
                videoId = videoId.Value.Value,
                context = new
                {
                    client = new
                    {
                        clientName = "ANDROID_TESTSUITE",
                        clientVersion = "1.9",
                        hl = "en",
                        gl = "US",
                        osName = "Android",
                        osVersion = "15",
                        deviceMake = "Google",
                        deviceModel = "Pixel 9 Pro",
                        androidSdkVersion = "35"
                    }
                }
            };

            string jsonString = JsonSerializer.Serialize(payload, _jsonOptions);
            request.Content = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _innerTubeClient.SendAsync(request, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Extract the fast videoDetails object
            if (root.TryGetProperty("videoDetails", out JsonElement videoDetails) &&
                videoDetails.TryGetProperty("lengthSeconds", out JsonElement lengthSeconds))
            {
                if (long.TryParse(lengthSeconds.GetString(), out long secs))
                {
                    return TimeSpan.FromSeconds(secs);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InnerTube Duration] Error resolving duration for {trackUrl}: {ex.Message}");
        }

        // Fallback to YoutubeExplode
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));

            Video videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            return videoInfo.Duration;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YoutubeExplode Duration] Error: {ex.Message}");
        }

        return null;
    }

    private async Task<ResolvedMedia?> GetFastStreamUrlAsync(string videoId)
    {
        InnerTubeClientInfo[] clients = new InnerTubeClientInfo[]
        {
            new InnerTubeClientInfo { Name = "ANDROID_VR", Version = "1.37", Id = "28", OsName = "Android", OsVersion = "12", Make = "Oculus", Model = "Quest 3", Sdk = "32", UA = "com.google.android.apps.youtube.vr.oculus/1.37 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/107.0.5284.2)" },
            new InnerTubeClientInfo { Name = "ANDROID_TESTSUITE", Version = "1.9", Id = "30", OsName = "Android", OsVersion = "15", Make = "Google", Model = "Pixel 9 Pro", Sdk = "35", UA = "com.google.android.youtube/1.9 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002) gzip" },
            new InnerTubeClientInfo { Name = "ANDROID_UNPLUGGED", Version = "8.49.0", Id = "29", OsName = "Android", OsVersion = "15", Make = "Google", Model = "Pixel 9 Pro", Sdk = "35", UA = "com.google.android.apps.youtube.unplugged/8.49.0 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip" },
            new InnerTubeClientInfo { Name = "VISIONOS", Version = "0.1", Id = "101", OsName = "visionOS", OsVersion = "1.3.21O771", Make = "Apple", Model = "RealityDevice14,1", Sdk = null, UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15" }
        };

        foreach (InnerTubeClientInfo clientConfig in clients)
        {
            for (int attempt = 1; attempt <= MaxClientAttempts; attempt++)
            {
                try
                {
                    string baseUrl = clientConfig.Name.Contains("MUSIC") || clientConfig.Name == "WEB_REMIX"
                        ? "https://music.youtube.com"
                        : "https://www.youtube.com";

                    string apiUrl = $"{baseUrl}/youtubei/v1/player?key={InnerTubeApiKey}&prettyPrint=false";

                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                    request.Headers.Add("User-Agent", clientConfig.UA);
                    request.Headers.Add("Accept", "application/json");
                    request.Headers.Add("X-Goog-Api-Format-Version", "1");
                    request.Headers.Add("X-YouTube-Client-Name", clientConfig.Id);
                    request.Headers.Add("X-YouTube-Client-Version", clientConfig.Version);

                    if (clientConfig.Name.StartsWith("WEB") || clientConfig.Name == "VISIONOS")
                    {
                        request.Headers.Add("X-Origin", baseUrl);
                        request.Headers.Add("Referer", $"{baseUrl}/");
                    }

                    if (!string.IsNullOrEmpty(_visitorData))
                        request.Headers.Add("X-Goog-Visitor-Id", _visitorData);

                    Dictionary<string, string> clientObj = new Dictionary<string, string>
                    {
                        { "clientName", clientConfig.Name },
                        { "clientVersion", clientConfig.Version },
                        { "hl", "en" },
                        { "gl", "US" }
                    };

                    if (clientConfig.OsName != null) clientObj["osName"] = clientConfig.OsName;
                    if (clientConfig.OsVersion != null) clientObj["osVersion"] = clientConfig.OsVersion;
                    if (clientConfig.Make != null) clientObj["deviceMake"] = clientConfig.Make;
                    if (clientConfig.Model != null) clientObj["deviceModel"] = clientConfig.Model;
                    if (clientConfig.Sdk != null) clientObj["androidSdkVersion"] = clientConfig.Sdk;
                    if (!string.IsNullOrEmpty(_visitorData)) clientObj["visitorData"] = _visitorData;

                    object payload = new
                    {
                        videoId = videoId,
                        context = new
                        {
                            client = clientObj,
                            user = new { }
                        }
                    };

                    string jsonString = JsonSerializer.Serialize(payload, _jsonOptions);
                    request.Content = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await _innerTubeClient.SendAsync(request);
                    string json = await response.Content.ReadAsStringAsync();

                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("responseContext", out JsonElement rc) && rc.TryGetProperty("visitorData", out JsonElement vd))
                    {
                        string newVd = vd.GetString()!;
                        if (_visitorData != newVd)
                        {
                            _visitorData = newVd;
                        }
                    }

                    if (root.TryGetProperty("playabilityStatus", out JsonElement statusProp) &&
                        statusProp.TryGetProperty("status", out JsonElement status) &&
                        status.GetString() != "OK")
                    {
                        string statusStr = status.GetString()!;

                        if (attempt == 1 && statusStr == "LOGIN_REQUIRED" && !string.IsNullOrEmpty(_visitorData))
                            continue;

                        break;
                    }

                    if (root.TryGetProperty("streamingData", out JsonElement streamingData) &&
                        streamingData.TryGetProperty("adaptiveFormats", out JsonElement adaptiveFormats))
                    {
                        JsonElement bestAudio = adaptiveFormats.EnumerateArray()
                            .Where(f => f.TryGetProperty("mimeType", out JsonElement mime) && mime.GetString()!.Contains("audio/"))
                            .Where(f => f.TryGetProperty("url", out _))
                            .OrderByDescending(f =>
                            {
                                int score = f.TryGetProperty("bitrate", out JsonElement b) ? b.GetInt32() : 0;
                                string mime = f.TryGetProperty("mimeType", out JsonElement m) ? m.GetString() ?? "" : "";

                                if (mime.Contains("webm") || mime.Contains("opus"))
                                    score += PreferredAudioBitrateScore;

                                return score;
                            })
                            .FirstOrDefault();

                        if (bestAudio.ValueKind != JsonValueKind.Undefined && bestAudio.TryGetProperty("url", out JsonElement urlProp))
                        {
                            Debug.WriteLine($"[Resolve] FAST PATH SUCCESS! Resolved direct stream using client: {clientConfig.Name}");
                            return new ResolvedMedia(urlProp.GetString()!, clientConfig.UA);
                        }
                    }
                    break;
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
        return null;
    }

    private class InnerTubeClientInfo
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
}