using LibVLCSharp.Shared;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private static readonly SemaphoreSlim _ytSemaphore = new(4, 4);
    private static readonly SemaphoreSlim _vlcParseSemaphore = new(1, 1);

    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string RawUrl, DateTime FetchedAt)> _streamUrlCache = new();

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private static LibVLC? _probeVlc;

    private long _lastTimeUpdateTicks = 0;


    private void InitializeVLCEngine()
    {
        if (_libVLC != null) return;

        // --http-reconnect so VLC automatically retries connections
        _libVLC = new LibVLC("--no-video", "--http-reconnect");
        _player = new MediaPlayer(_libVLC);

        // Convert VLC's absolute time to relative time for sliced tracks
        _player.TimeChanged += (s, e) =>
        {
            long currentTicks = Environment.TickCount64;
            if (currentTicks - _lastTimeUpdateTicks < 500) return;
            _lastTimeUpdateTicks = currentTicks;

            double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
            double relativeTimeMs = e.Time - startMs;
            if (relativeTimeMs < 0) relativeTimeMs = 0;

            // Push UI-bound property updates to the Main Thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                CurrentPosition = TimeSpan.FromMilliseconds(relativeTimeMs);
                if (!_isDragging)
                {
                    CurrentPositionSeconds = CurrentPosition.TotalSeconds;
                }

                // Sync lyrics highlighting
                SyncActiveLyric(CurrentPosition);
            });
        };

        // Calculate relative duration for sliced tracks
        _player.LengthChanged += (s, e) =>
        {
            double totalSeconds = e.Length / 1000.0;
            double end = CurrentTrack?.EndTime ?? totalSeconds;
            double start = CurrentTrack?.StartTime ?? 0;
            double duration = end - start;
            if (duration <= 0) duration = totalSeconds; // Fallback

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Duration = TimeSpan.FromSeconds(duration);
                DurationSeconds = duration;

                if (CurrentTrack != null)
                {
                    bool changed = CurrentTrack.Duration != Duration;
                    CurrentTrack.Duration = Duration;

                    if (changed)
                    {
                        // Cache the newly discovered length into JSON
                        RequestSaveLibrary();
                    }
                }
            });

            if (CurrentTrack != null)
            {
                // Decide which icon to use based on the playlist's IconType setting
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
                        if (videoId != null)
                        {
                            ytThumb = $"https://img.youtube.com/vi/{videoId.Value}/hqdefault.jpg";
                        }
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

        _player.Playing += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = true;
                IsLoadingMedia = false;
                SyncOSPlaybackState();
            });
            ReleaseTransientWakeLock();
        };

        _player.Paused += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _player.Stopped += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _player.EncounteredError += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = false;
            });
            ReleaseTransientWakeLock();
        };

        _player.EndReached += (s, e) =>
        {
            AcquireTransientWakeLock();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = true;
                // Pass "Natural" so the system knows to loop if Repeat One is active
                PlayNextCommand.Execute("Natural");
            });
        };
    }

    private async Task PreloadNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        // Wait before preloading! 
        // This gives VLC time to build up a network buffer 
        // before YoutubeExplode hogs the CPU/Network.
        await Task.Delay(15000);

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        // Silently resolve and cache the next track's URL in the background
        await ResolveMediaSourceAsync(nextTrack);
    }

    private CancellationTokenSource? _mediaLoadCts;
    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        // 1. Cancel any currently loading media immediately
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

        // 2. Fetch the URL
        string? rawAudioUrl = await ResolveMediaSourceAsync(newValue);

        // 3. Check if the user skipped this track while we were waiting!
        if (token.IsCancellationRequested)
        {
            ReleaseTransientWakeLock();
            return; // Abort silently!
        }

        if (rawAudioUrl == null)
        {
            IsLoadingMedia = false;
            ReleaseTransientWakeLock();
            return;
        }

        var media = new Media(_libVLC!, new Uri(rawAudioUrl));

        // --- FIX FOR BACKGROUND SKIPPING & AUDIO CUTOUTS ---

        // 1. Increase network caching drastically to 3000ms (3 seconds)
        // to survive OS background CPU/Network throttling cycles (Doze).
        media.AddOption(":network-caching=3000");

        // 2. Disable clock synchronization and jitter dropping entirely.
        // This prevents VLC from dropping "late" packets and skipping multiple 
        // seconds ahead when the OS momentarily starves the CPU in the background.
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

    private async Task<string?> ResolveMediaSourceAsync(PlaylistItem track)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;

        if (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be"))
        {
            // 1. FAST PATH: Check if we already preloaded this URL
            if (_streamUrlCache.TryGetValue(track.Url, out var cached) && (DateTime.Now - cached.FetchedAt).TotalHours < 2)
            {
                return cached.RawUrl;
            }

            // 2. SLOW PATH: Fetch it from YouTube
            try
            {
                var rawAudioUrl = await Task.Run(async () =>
                {
                    var videoId = YoutubeExplode.Videos.VideoId.Parse(track.Url);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, cts.Token);
                    return manifest.GetAudioOnlyStreams().GetWithHighestBitrate()?.Url;
                });

                if (rawAudioUrl != null)
                {
                    // Save it to the cache for later
                    _streamUrlCache[track.Url] = (rawAudioUrl, DateTime.Now);
                }

                return rawAudioUrl;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                return null;
            }
        }
        return track.Url;
    }

    private async Task ResolvePlaylistDurationsAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        if (playlist == null) return;

        // CRITICAL FIX: Snapshot items on the Main Thread to avoid JNI/Threading crashes 
        // when the user interacts with SwipeViews while this starts.
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

        // Move off the main thread entirely
        await Task.Run(async () =>
        {
            // Lock at the playlist level so we don't process 5 playlists at once if the user clicks around rapidly
            try
            {
                await _vlcParseSemaphore.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return; // User navigated away before this even started
            }

            try
            {
                if (_probeVlc == null)
                {
                    _probeVlc = new LibVLC("--no-video", "--no-audio", "--network-caching=500");
                }

                var itemsToResolve = playlist.Items
                    .Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url))
                    .ToList();

                if (!itemsToResolve.Any()) return;

                bool needsSave = false;

                // ALLOW CONCURRENCY: Process up to 4 tracks at the exact same time.
                // This is fast enough to feel responsive, but low enough to not trigger YouTube's bot protection.
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
                                cts.CancelAfter(TimeSpan.FromSeconds(10)); // Timeout after 10s

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

                                // CRITICAL FIX: Read the duration NOW, on this thread, before we dispose the media object
                                long mediaDurationMs = media.Duration;

                                if (mediaDurationMs > 0)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        double start = track.StartTime ?? 0;
                                        // Use the safely captured variable here instead of media.Duration!
                                        double durationSecs = (mediaDurationMs / 1000.0) - start;
                                        track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                                    });
                                    isSuccess = true;
                                }

                                // Now it is 100% safe to dispose, because the UI thread isn't trying to read it anymore.
                                _ = Task.Run(() => { try { media.Dispose(); } catch { } });
                            }
                            else
                            {
                                // Timeout or Cancelled: safely dispose in background once parse finishes
                                _ = parseTask.ContinueWith(_ =>
                                {
                                    _ = Task.Run(() => { try { media.Dispose(); } catch { } });
                                }, TaskScheduler.Default);
                            }
                        }

                        // ONLY mark as attempted (removing the +) if it successfully got the duration
                        if (isSuccess)
                        {
                            track.IsResolutionAttempted = true;
                            needsSave = true;
                            playlist.RequestRecalculateDuration();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled, leave IsResolutionAttempted = false (Keep the +)
                    }
                    catch (Exception ex)
                    {
                        // HTTP/Network error, leave IsResolutionAttempted = false (Keep the +)
                        Debug.WriteLine($"[Resolve] Error resolving duration for {track.Url}: {ex.Message}");
                    }
                    finally
                    {
                        concurrencyLimit.Release();
                    }
                });

                // Wait for the batch of tasks to finish
                await Task.WhenAll(tasks);

                // Save the library to disk once everything is done
                if (needsSave)
                {
                    RequestSaveLibrary();
                }
            }
            finally
            {
                _vlcParseSemaphore.Release();
            }
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