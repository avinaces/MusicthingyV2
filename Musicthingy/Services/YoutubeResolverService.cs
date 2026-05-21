using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.Services;

/// <summary>
/// Service responsible for resolving direct media stream URLs and metadata from YouTube.
/// Contains fallback strategies to emulate client behavior and bypass restrictions.
/// </summary>
public class YouTubeResolverService
{
    private const double CacheExpiryHours = 2.0;
    private const int ManifestFetchTimeoutSeconds = 60;
    private const int VideoInfoFetchTimeoutSeconds = 10;
    private const int MaxClientAttempts = 2;
    private const int PreferredAudioBitrateScore = 1000000;

    private const string InnerTubeApiUrl = "https://music.youtube.com/youtubei/v1/player?key=AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w&prettyPrint=false";
    private const string YouTubeOrigin = "https://music.youtube.com";

    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string RawUrl, DateTime FetchedAt)> _streamUrlCache = new();

    private static readonly HttpClient _innerTubeClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });

    private static string? _visitorData = null;
    private static readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<string?> ResolveMediaSourceAsync(string trackUrl)
    {
        if (string.IsNullOrEmpty(trackUrl))
            return null;

        if (trackUrl.Contains("youtube.com") || trackUrl.Contains("youtu.be"))
        {
            if (_streamUrlCache.TryGetValue(trackUrl, out (string RawUrl, DateTime FetchedAt) cached)
                && (DateTime.Now - cached.FetchedAt).TotalHours < CacheExpiryHours)
            {
                Debug.WriteLine($"[Resolve] CACHE HIT");
                return cached.RawUrl;
            }

            string? resolvedUrl = null;
            VideoId? videoId = VideoId.TryParse(trackUrl);

            if (videoId != null)
            {
                resolvedUrl = await GetFastStreamUrlAsync(videoId.Value.Value);

                if (string.IsNullOrEmpty(resolvedUrl))
                {
                    try
                    {
                        resolvedUrl = await Task.Run(async () =>
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
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE ERROR: {ex.Message}");
                    }
                }
            }

            if (string.IsNullOrEmpty(resolvedUrl))
            {
                resolvedUrl = trackUrl;
            }
            else if (resolvedUrl != trackUrl)
            {
                _streamUrlCache[trackUrl] = (resolvedUrl, DateTime.Now);
            }

            return resolvedUrl;
        }

        return trackUrl;
    }

    public async Task<TimeSpan?> GetVideoDurationAsync(string trackUrl, CancellationToken cancellationToken)
    {
        VideoId? videoId = VideoId.TryParse(trackUrl);

        if (videoId != null)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(VideoInfoFetchTimeoutSeconds));

            Video videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
            return videoInfo.Duration;
        }

        return null;
    }

    private async Task<string?> GetFastStreamUrlAsync(string videoId)
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
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, InnerTubeApiUrl);
                    request.Headers.Add("User-Agent", clientConfig.UA);
                    request.Headers.Add("Accept", "application/json");
                    request.Headers.Add("X-Goog-Api-Format-Version", "1");
                    request.Headers.Add("X-YouTube-Client-Name", clientConfig.Id);
                    request.Headers.Add("X-YouTube-Client-Version", clientConfig.Version);
                    request.Headers.Add("X-Origin", YouTubeOrigin);
                    request.Headers.Add("Referer", $"{YouTubeOrigin}/");

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

                    Dictionary<string, object> payload = new Dictionary<string, object>
                    {
                        { "videoId", videoId },
                        { "context", new Dictionary<string, object>
                            {
                                { "client", clientObj },
                                { "user", new Dictionary<string, object>() }
                            }
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
                        {
                            continue;
                        }

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
                            return urlProp.GetString();
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