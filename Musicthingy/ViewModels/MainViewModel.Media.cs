using LibVLCSharp.Shared;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private static readonly SemaphoreSlim _ytSemaphore = new(4, 4);
    private static readonly SemaphoreSlim _vlcParseSemaphore = new(1, 1);

    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string RawUrl, DateTime FetchedAt)> _streamUrlCache = new();

    // Configured to emulate OkHttp's underlying behavior
    private static readonly HttpClient _innerTubeClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });

    // We store the dynamically bound tracking token here just like OpenTune does
    private static string? _visitorData = null;

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private static LibVLC? _probeVlc;

    private long _lastTimeUpdateTicks = 0;


    private void InitializeVLCEngine()
    {
        if (_libVLC != null) return;

        _libVLC = new LibVLC("--no-video", "--http-reconnect");
        _player = new MediaPlayer(_libVLC);

        _player.TimeChanged += (s, e) =>
        {
            long currentTicks = Environment.TickCount64;
            if (currentTicks - _lastTimeUpdateTicks < 500) return;
            _lastTimeUpdateTicks = currentTicks;

            double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
            double relativeTimeMs = e.Time - startMs;
            if (relativeTimeMs < 0) relativeTimeMs = 0;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                CurrentPosition = TimeSpan.FromMilliseconds(relativeTimeMs);
                if (!_isDragging) CurrentPositionSeconds = CurrentPosition.TotalSeconds;
                SyncActiveLyric(CurrentPosition);
            });
        };

        _player.LengthChanged += (s, e) =>
        {
            double totalSeconds = e.Length / 1000.0;
            double end = CurrentTrack?.EndTime ?? totalSeconds;
            double start = CurrentTrack?.StartTime ?? 0;
            double duration = end - start;
            if (duration <= 0) duration = totalSeconds;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Duration = TimeSpan.FromSeconds(duration);
                DurationSeconds = duration;

                if (CurrentTrack != null)
                {
                    bool changed = CurrentTrack.Duration != Duration;
                    CurrentTrack.Duration = Duration;
                    if (changed) RequestSaveLibrary();
                }
            });

            if (CurrentTrack != null)
            {
                string widgetIconUrl = string.Empty;
                var pl = CurrentTrack.Playlist;
                var iconType = pl?.IconType ?? PlaylistIconType.Playlist;

                if (iconType == PlaylistIconType.Track)
                {
                    widgetIconUrl = !string.IsNullOrEmpty(CurrentTrack.IconUrl) ? CurrentTrack.IconUrl : pl?.IconUrl ?? string.Empty;
                }
                else if (iconType == PlaylistIconType.YouTube)
                {
                    string? ytThumb = null;
                    if (!string.IsNullOrEmpty(CurrentTrack.Url) && (CurrentTrack.Url.Contains("youtube.com") || CurrentTrack.Url.Contains("youtu.be")))
                    {
                        var videoId = YoutubeExplode.Videos.VideoId.TryParse(CurrentTrack.Url);
                        if (videoId != null) ytThumb = $"https://img.youtube.com/vi/{videoId.Value}/hqdefault.jpg";
                    }
                    widgetIconUrl = !string.IsNullOrEmpty(ytThumb) ? ytThumb : pl?.IconUrl ?? CurrentTrack.IconUrl ?? string.Empty;
                }
                else
                {
                    widgetIconUrl = !string.IsNullOrEmpty(pl?.IconUrl) ? pl.IconUrl : CurrentTrack.IconUrl ?? string.Empty;
                }

                _ = UpdateOSMediaWidgetsAsync(CurrentTrack.Title, pl?.Name ?? "Musicthingy", widgetIconUrl);
            }
        };

        _player.Playing += (s, e) => { MainThread.BeginInvokeOnMainThread(() => { IsPlaying = true; IsLoadingMedia = false; SyncOSPlaybackState(); }); ReleaseTransientWakeLock(); };
        _player.Paused += (s, e) => { MainThread.BeginInvokeOnMainThread(() => { IsPlaying = false; SyncOSPlaybackState(); }); if (!IsLoadingMedia) ReleaseTransientWakeLock(); };
        _player.Stopped += (s, e) => { MainThread.BeginInvokeOnMainThread(() => { IsPlaying = false; SyncOSPlaybackState(); }); if (!IsLoadingMedia) ReleaseTransientWakeLock(); };

        _player.EncounteredError += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() => { IsLoadingMedia = false; PlayNextCommand.Execute("Error"); Debug.WriteLine("Failed to play media"); });
            ReleaseTransientWakeLock();
        };

        _player.EndReached += (s, e) =>
        {
            AcquireTransientWakeLock();
            MainThread.BeginInvokeOnMainThread(() => { IsLoadingMedia = true; PlayNextCommand.Execute("Natural"); });
        };
    }

    private async Task PreloadNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        await Task.Delay(15000);

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        await ResolveMediaSourceAsync(nextTrack);
    }

    private CancellationTokenSource? _mediaLoadCts;
    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        _mediaLoadCts?.Cancel();
        _mediaLoadCts = new CancellationTokenSource();
        var token = _mediaLoadCts.Token;

        if (IsAutoFollowEnabled) TriggerAutoFollow();

        IsLoadingMedia = true;
        AcquireTransientWakeLock();

        InitializeVLCEngine();
        _player!.Stop();

        CurrentPosition = TimeSpan.Zero;
        CurrentPositionSeconds = 0;
        SyncOSPlaybackState();

        string? rawAudioUrl = await ResolveMediaSourceAsync(newValue);

        if (token.IsCancellationRequested)
        {
            ReleaseTransientWakeLock();
            return;
        }

        Media media = new Media(_libVLC!, new Uri(rawAudioUrl));

        media.AddOption(":network-caching=3000");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");

        if (newValue.StartTime.HasValue) media.AddOption($":start-time={newValue.StartTime.Value}");
        if (newValue.EndTime.HasValue) media.AddOption($":stop-time={newValue.EndTime.Value}");

        if (_initialRestorePosition > 0)
        {
            double absoluteRestore = _initialRestorePosition + (newValue.StartTime ?? 0);
            media.AddOption($":start-time={absoluteRestore}");
            _initialRestorePosition = 0;
        }

        _player.Play(media);
        _ = Task.Run(PreloadNextTrackAsync);
    }

    private async Task<string?> GetFastStreamUrlAsync(string videoId)
    {
        // 4 heavily reliable NO-AUTH fallbacks ported directly from OpenTune
        var clients = new[]
        {
            new {
                Name = "ANDROID_VR", Version = "1.37", Id = "28",
                OsName = "Android", OsVersion = "12", Make = "Oculus", Model = "Quest 3", Sdk = (string?)"32",
                UA = "com.google.android.apps.youtube.vr.oculus/1.37 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/107.0.5284.2)"
            },
            new {
                Name = "ANDROID_TESTSUITE", Version = "1.9", Id = "30",
                OsName = "Android", OsVersion = "15", Make = "Google", Model = "Pixel 9 Pro", Sdk = (string?)"35",
                UA = "com.google.android.youtube/1.9 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002) gzip"
            },
            new {
                Name = "ANDROID_UNPLUGGED", Version = "8.49.0", Id = "29",
                OsName = "Android", OsVersion = "15", Make = "Google", Model = "Pixel 9 Pro", Sdk = (string?)"35",
                UA = "com.google.android.apps.youtube.unplugged/8.49.0 (Linux; U; Android 15; en_US; Pixel 9 Pro; Build/AP4A.250205.002; Cronet/132.0.6834.79) gzip"
            },
            new {
                Name = "VISIONOS", Version = "0.1", Id = "101",
                OsName = "visionOS", OsVersion = "1.3.21O771", Make = "Apple", Model = "RealityDevice14,1", Sdk = (string?)null,
                UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
            }
        };

        string origin = "https://music.youtube.com";
        string apiUrl = $"{origin}/youtubei/v1/player?key=AIzaSyA8eiZmM1FaDVjRy-df2KTyQ_vz_yYM39w&prettyPrint=false";

        // Emulate OpenTune's 'explicitNulls = false' to avoid BotGuard schema rejection
        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        foreach (var c in clients)
        {
            // We allow up to 2 attempts per client. The first attempt might fail but yields a bound visitorData
            int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                    request.Headers.Add("User-Agent", c.UA);
                    request.Headers.Add("Accept", "application/json");
                    request.Headers.Add("X-Goog-Api-Format-Version", "1");
                    request.Headers.Add("X-YouTube-Client-Name", c.Id);
                    request.Headers.Add("X-YouTube-Client-Version", c.Version);
                    request.Headers.Add("X-Origin", origin);
                    request.Headers.Add("Referer", $"{origin}/");

                    if (!string.IsNullOrEmpty(_visitorData))
                        request.Headers.Add("X-Goog-Visitor-Id", _visitorData);

                    // Reconstruct OpenTune's Context object without any null fields
                    var clientObj = new Dictionary<string, string>
                    {
                        { "clientName", c.Name },
                        { "clientVersion", c.Version },
                        { "hl", "en" },
                        { "gl", "US" }
                    };

                    if (c.OsName != null) clientObj["osName"] = c.OsName;
                    if (c.OsVersion != null) clientObj["osVersion"] = c.OsVersion;
                    if (c.Make != null) clientObj["deviceMake"] = c.Make;
                    if (c.Model != null) clientObj["deviceModel"] = c.Model;
                    if (c.Sdk != null) clientObj["androidSdkVersion"] = c.Sdk;
                    if (!string.IsNullOrEmpty(_visitorData)) clientObj["visitorData"] = _visitorData;

                    var payload = new
                    {
                        videoId = videoId,
                        context = new
                        {
                            client = clientObj,
                            user = new { }
                        }
                    };

                    // Use StringContent to explicitly generate the correct Content-Length header like OkHttp
                    string jsonString = JsonSerializer.Serialize(payload, jsonOptions);
                    request.Content = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");

                    var response = await _innerTubeClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // 1. DYNAMIC VISITOR BINDING: Extract the token YouTube assigns us on failure
                    if (root.TryGetProperty("responseContext", out var rc) && rc.TryGetProperty("visitorData", out var vd))
                    {
                        string newVd = vd.GetString()!;
                        if (_visitorData != newVd)
                        {
                            Debug.WriteLine($"[FastPath] Client {c.Name} obtained new bound visitorData: {newVd}");
                            _visitorData = newVd;
                        }
                    }

                    // 2. CHECK STATUS
                    if (root.TryGetProperty("playabilityStatus", out var statusProp) &&
                        statusProp.TryGetProperty("status", out var status) &&
                        status.GetString() != "OK")
                    {
                        string statusStr = status.GetString()!;
                        string reason = statusProp.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() ?? "N/A" : "N/A";

                        Debug.WriteLine($"[FastPath] Client {c.Name} Blocked: {statusStr} (Reason: {reason})");

                        // 3. THE TRICK: If BotGuard blocks us but gives us a session token, we instantly bounce back 
                        // with that token to prove we are stateful!
                        if (attempt == 1 && statusStr == "LOGIN_REQUIRED" && !string.IsNullOrEmpty(_visitorData))
                        {
                            Debug.WriteLine($"[FastPath] Retrying Client {c.Name} with newly bound visitorData...");
                            continue; // Retry this specific client!
                        }

                        break; // If it fails twice, or for another reason, move to the next client
                    }

                    // 4. EXTRACT STREAMS
                    if (root.TryGetProperty("streamingData", out var streamingData) &&
                        streamingData.TryGetProperty("adaptiveFormats", out var adaptiveFormats))
                    {
                        var bestAudio = adaptiveFormats.EnumerateArray()
                            .Where(f => f.TryGetProperty("mimeType", out var mime) && mime.GetString()!.Contains("audio/"))
                            .Where(f => f.TryGetProperty("url", out _)) // Must have a direct URL, no ciphers
                            .OrderByDescending(f => f.TryGetProperty("bitrate", out var b) ? b.GetInt32() : 0)
                            .FirstOrDefault();

                        if (bestAudio.ValueKind != JsonValueKind.Undefined && bestAudio.TryGetProperty("url", out var urlProp))
                        {
                            Debug.WriteLine($"[FastPath] SUCCESS! Resolved direct stream using client: {c.Name} (Attempt {attempt})");
                            return urlProp.GetString();
                        }
                    }

                    Debug.WriteLine($"[FastPath] Client {c.Name} returned NO direct streams.");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FastPath] Client {c.Name} Exception: {ex.Message}");
                    break;
                }
            }
        }

        Debug.WriteLine("[FastPath] ALL Fast Clients failed. Triggering YoutubeExplode...");
        return null;
    }

    private async Task<string?> ResolveMediaSourceAsync(PlaylistItem track)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;

        if (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be"))
        {
            Stopwatch totalSw = Stopwatch.StartNew();
            Debug.WriteLine($"[Resolve] Starting resolution for: {track.Url}");

            if (_streamUrlCache.TryGetValue(track.Url, out var cached) && (DateTime.Now - cached.FetchedAt).TotalHours < 2)
            {
                Debug.WriteLine($"[Resolve] CACHE HIT - Took {totalSw.ElapsedMilliseconds}ms");
                return cached.RawUrl;
            }

            string? resolvedUrl = null;
            var videoId = YoutubeExplode.Videos.VideoId.TryParse(track.Url);

            if (videoId != null)
            {
                // FAST PATH: Rapid Fallback Loop
                Stopwatch fastSw = Stopwatch.StartNew();
                Debug.WriteLine("[Resolve] Attempting Fast InnerTube Path...");
                resolvedUrl = await GetFastStreamUrlAsync(videoId.Value.Value);
                fastSw.Stop();

                if (!string.IsNullOrEmpty(resolvedUrl))
                {
                    Debug.WriteLine($"[Resolve] FAST PATH SUCCESS - Took {fastSw.ElapsedMilliseconds}ms (Total: {totalSw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    Debug.WriteLine($"[Resolve] FAST PATH FAILED - Took {fastSw.ElapsedMilliseconds}ms. Falling back to YoutubeExplode...");

                    // SLOW PATH: YoutubeExplode Fetch
                    Stopwatch ytSw = Stopwatch.StartNew();
                    try
                    {
                        var rawAudioUrl = await Task.Run(async () =>
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId.Value, cts.Token);
                            return manifest.GetAudioOnlyStreams().GetWithHighestBitrate()?.Url;
                        });

                        resolvedUrl = rawAudioUrl;
                        ytSw.Stop();
                        Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE SUCCESS - Took {ytSw.ElapsedMilliseconds}ms (Total: {totalSw.ElapsedMilliseconds}ms)");
                    }
                    catch (Exception ex)
                    {
                        ytSw.Stop();
                        Debug.WriteLine($"[Resolve] YOUTUBE EXPLODE ERROR: {ex.Message} (Took {ytSw.ElapsedMilliseconds}ms)");
                    }
                }
            }

            if (string.IsNullOrEmpty(resolvedUrl))
            {
                Debug.WriteLine($"[Resolve] ALL METHODS FAILED - Returning raw YouTube URL to LibVLC (Total time: {totalSw.ElapsedMilliseconds}ms)");
                resolvedUrl = track.Url;
            }
            else if (resolvedUrl != track.Url)
            {
                _streamUrlCache[track.Url] = (resolvedUrl, DateTime.Now);
            }

            totalSw.Stop();
            Debug.WriteLine($"[Resolve] Finished resolution chain in {totalSw.ElapsedMilliseconds}ms");

            return resolvedUrl;
        }
        return track.Url;
    }

    private async Task ResolvePlaylistDurationsAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        if (playlist == null) return;

        List<PlaylistItem> itemsToResolve = new();
        if (MainThread.IsMainThread)
        {
            itemsToResolve = playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList();
        }
        else
        {
            var tcs = new TaskCompletionSource<List<PlaylistItem>>();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { tcs.SetResult(playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList()); }
                catch { tcs.SetResult(new()); }
            });
            itemsToResolve = await tcs.Task;
        }

        if (!itemsToResolve.Any()) return;

        await Task.Run(async () =>
        {
            try { await _vlcParseSemaphore.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (_probeVlc == null) _probeVlc = new LibVLC("--no-video", "--no-audio", "--network-caching=500");

                var itemsToResolve = playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList();
                if (!itemsToResolve.Any()) return;

                bool needsSave = false;
                var concurrencyLimit = new SemaphoreSlim(4, 4);

                var tasks = itemsToResolve.Select(async track =>
                {
                    await concurrencyLimit.WaitAsync(cancellationToken);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        bool isSuccess = false;

                        if (track.EndTime.HasValue)
                        {
                            double end = track.EndTime.Value;
                            double start = track.StartTime ?? 0;
                            if (end > start)
                            {
                                MainThread.BeginInvokeOnMainThread(() => track.Duration = TimeSpan.FromSeconds(end - start));
                                isSuccess = true;
                            }
                        }

                        if (!isSuccess && (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be")))
                        {
                            var videoId = YoutubeExplode.Videos.VideoId.TryParse(track.Url);
                            if (videoId != null)
                            {
                                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                cts.CancelAfter(TimeSpan.FromSeconds(10));

                                var videoInfo = await _youtubeClient.Videos.GetAsync(videoId.Value, cts.Token);
                                if (videoInfo.Duration.HasValue)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        double start = track.StartTime ?? 0;
                                        double durationSecs = videoInfo.Duration.Value.TotalSeconds - start;
                                        track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                                    });
                                    isSuccess = true;
                                }
                            }
                        }
                        else if (!isSuccess)
                        {
                            var media = new Media(_probeVlc, new Uri(track.Url));
                            var parseTask = media.Parse(MediaParseOptions.ParseNetwork);

                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var delayTask = Task.Delay(5000, delayCts.Token);

                            var completedTask = await Task.WhenAny(parseTask, delayTask);

                            if (completedTask == parseTask)
                            {
                                delayCts.Cancel();
                                long mediaDurationMs = media.Duration;

                                if (mediaDurationMs > 0)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        double start = track.StartTime ?? 0;
                                        double durationSecs = (mediaDurationMs / 1000.0) - start;
                                        track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                                    });
                                    isSuccess = true;
                                }

                                _ = Task.Run(() => { try { media.Dispose(); } catch { } });
                            }
                            else
                            {
                                _ = parseTask.ContinueWith(_ => { _ = Task.Run(() => { try { media.Dispose(); } catch { } }); }, TaskScheduler.Default);
                            }
                        }

                        if (isSuccess)
                        {
                            track.IsResolutionAttempted = true;
                            needsSave = true;
                            playlist.RequestRecalculateDuration();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Debug.WriteLine($"[Resolve] Error resolving duration for {track.Url}: {ex.Message}"); }
                    finally { concurrencyLimit.Release(); }
                });

                await Task.WhenAll(tasks);
                if (needsSave) RequestSaveLibrary();
            }
            finally { _vlcParseSemaphore.Release(); }
        });
    }

    private static void CleanupCache()
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            var files = Directory.GetFiles(cacheDir, "*.mp4");
            foreach (var file in files)
            {
                if (File.GetCreationTime(file) < DateTime.Now.AddDays(-3))
                    File.Delete(file);
            }
        }
        catch { }
    }
}