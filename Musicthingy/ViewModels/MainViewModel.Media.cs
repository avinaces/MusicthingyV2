using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Diagnostics;
using System.Threading;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const int PreloadTrackDelayMs = 15000;
    private const int MinimumTimeUpdateIntervalMs = 64;
    private const int ParseDelayTimeoutMs = 5000;
    private const int MaxSeekDesyncToleranceMs = 1500;

    private readonly YouTubeResolverService _youtubeResolver;
    private readonly IMediaPlaybackService _playbackService;
    private readonly IMediaProbingService _mediaProbingService;

    private Stopwatch? _playbackStartupSw;
    private CancellationTokenSource? _mediaLoadCts;
    private long _lastTimeUpdateTicks = 0;

    private readonly SemaphoreSlim _probeConcurrencyLimit = new SemaphoreSlim(4, 4);

#if WINDOWS
    private static LibVLCSharp.Shared.LibVLC? _probeVlc;
#endif

    // Temporary initialization method to map the constructor injection in a partial class setup
    private void InitializePlaybackService()
    {
        _playbackService.BufferingChanged += OnBufferingChanged;
        _playbackService.Playing += OnPlaying;
        _playbackService.Paused += OnPaused;
        _playbackService.Stopped += OnStopped;
        _playbackService.ErrorEncountered += OnErrorEncountered;
        _playbackService.EndReached += OnEndReached;
        _playbackService.TimeChanged += OnTimeChanged;
        _playbackService.LengthChanged += OnLengthChanged;
    }
    private void UnsubscribePlaybackService()
    {
        try
        {
            _playbackService.BufferingChanged -= OnBufferingChanged;
            _playbackService.Playing -= OnPlaying;
            _playbackService.Paused -= OnPaused;
            _playbackService.Stopped -= OnStopped;
            _playbackService.ErrorEncountered -= OnErrorEncountered;
            _playbackService.EndReached -= OnEndReached;
            _playbackService.TimeChanged -= OnTimeChanged;
            _playbackService.LengthChanged -= OnLengthChanged;
        }
        catch { }
    }

    private void OnBufferingChanged(object? sender, bool isBuffering)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (IsLoadingMedia != isBuffering)
                IsLoadingMedia = isBuffering;
        });
    }

    private void OnPlaying(object? sender, EventArgs? e)
    {
        Debug.WriteLine($"[Playback] Playing @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_pauseOnLoad)
            {
                // User pressed pause while it was loading. Enforce the pause now.
                _playbackService.Pause();
                _pauseOnLoad = false;
                IsLoadingMedia = false;
                IsPlaying = false;
                SyncOSPlaybackState();
                ReleaseTransientWakeLock();
                return;
            }

            IsPlaying = true;
            IsLoadingMedia = false;
            SyncOSPlaybackState();

            // Audio is actually rendering now: the audio HAL holds its own wake
            // lock while sound plays and ExoPlayer's wake mode covers buffering
            // windows, so our blanket partial wake lock is no longer needed.
            // Keeping it for the whole session used to block system suspend for
            // hours of background playback. The WiFi lock stays until
            // pause/stop to keep the stream alive.
            ReleaseCpuWakeLock();
        });
    }

    private void OnPaused(object? sender, EventArgs? e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPlaying = false;
            SyncOSPlaybackState();
        });
        if (!IsLoadingMedia) ReleaseTransientWakeLock();
    }

    private void OnStopped(object? sender, EventArgs? e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsPlaying = false;
            SyncOSPlaybackState();
        });
        if (!IsLoadingMedia) ReleaseTransientWakeLock();
    }

    private void OnErrorEncountered(object? sender, EventArgs? e)
    {
        Debug.WriteLine($"[Playback] EncounteredError @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsLoadingMedia = false;
            PlayNextCommand.Execute("Error");
        });
        ReleaseTransientWakeLock();
    }

    private void OnEndReached(object? sender, EventArgs? e)
    {
        // No wake lock here on purpose: ReloadMediaAsync acquires one for the
        // actual load. Acquiring here used to leak the lock whenever the
        // advance bailed out early (empty queue, repeat off at the end - the
        // queue-end path in OnCurrentTrackChanged tears everything down).
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsLoadingMedia = true;
            PlayNextCommand.Execute("Natural");
        });
    }

    private void OnTimeChanged(object? sender, double timeMs)
    {
        OnPlaybackTimeChanged(timeMs);
    }

    private void OnLengthChanged(object? sender, double lengthMs)
    {
        Debug.WriteLine($"[Playback] LengthChanged={lengthMs}ms @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
        MainThread.BeginInvokeOnMainThread(() => UpdateTrackDuration(lengthMs / 1000.0, CurrentTrack));
        UpdateMediaWidgetForTrack(CurrentTrack);
    }

    private async Task PreloadNextTrackAsync(CancellationToken cancellationToken)
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null)
            return;

        try
        {
            await Task.Delay(PreloadTrackDelayMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        PlaylistItem nextTrack = PlaybackQueue[nextIndex];

        if (!string.IsNullOrEmpty(nextTrack.Url) && !cancellationToken.IsCancellationRequested)
        {
            await _youtubeResolver.ResolveMediaSourceAsync(nextTrack.Url);
        }
    }

    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        _pauseOnLoad = false;
        _mediaLoadCts?.Cancel();
        _mediaLoadCts = new CancellationTokenSource();
        CancellationToken token = _mediaLoadCts.Token;

        Stopwatch totalSw = Stopwatch.StartNew();

        Debug.WriteLine($"[Playback] ===== START LOAD =====");
        Debug.WriteLine($"[Playback] Track: {newValue.Title} (Start: {newValue.StartTime}, End: {newValue.EndTime})");

        // ==========================================
        // LOCAL FILE PERMISSION CHECK
        // ==========================================
        if (!string.IsNullOrEmpty(newValue.Url) &&
           (newValue.Url.StartsWith("/") || newValue.Url.StartsWith("file://") || newValue.Url.StartsWith("content://")))
        {
            var status = await Permissions.CheckStatusAsync<Helpers.LocalFilesPermission>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Helpers.LocalFilesPermission>();
            }

            if (status != PermissionStatus.Granted)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await ShowConfirmAsync(
                        "Permission Denied",
                        "Storage permission is required to play local files.",
                        "OK",
                        ModalBackgroundTapBehavior.Confirm,
                        isDanger: true);
                });

                ReleaseTransientWakeLock();
                IsLoadingMedia = false;
                return;
            }
        }

        // Display cached duration instantly before initiating any async loads, if possible
        if (newValue.Duration.TotalSeconds > 0)
        {
            UpdateTrackDuration(newValue.Duration.TotalSeconds + (newValue.StartTime ?? 0), newValue);
        }
        else
        {
            Duration = TimeSpan.Zero;
            DurationSeconds = 1; // Safeguard from division by zero exceptions in UI components

            // Kick-off duration resolution as early as possible so it caches fast 
            _ = Task.Run(() => ResolveSingleTrackDurationAsync(newValue, token));
        }

        if (_playbackService.CanReuseMedia(newValue.Url))
        {
            Debug.WriteLine($"[Playback] Reusing existing media stream for URL: {newValue.Url}");

            double startMs = (newValue.StartTime ?? 0) * 1000.0;
            double currentMs = _playbackService.CurrentTimeMs;

            if (Math.Abs(currentMs - startMs) > MaxSeekDesyncToleranceMs)
            {
                _playbackService.Seek(startMs);
            }

            if (!_playbackService.IsPlaying)
            {
                AcquireTransientWakeLock();
                _playbackService.Play();
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = false;
                IsPlaying = true;
                CurrentPosition = TimeSpan.Zero;
                CurrentPositionSeconds = 0;

                // Sync correct end-start boundary length internally
                double lengthSecs = _playbackService.LengthMs > 0
                    ? (_playbackService.LengthMs / 1000.0)
                    : (CurrentTrack != null && CurrentTrack.Duration.TotalSeconds > 0
                        ? CurrentTrack.Duration.TotalSeconds + (CurrentTrack.StartTime ?? 0)
                        : 0);

                UpdateTrackDuration(lengthSecs, CurrentTrack);
                SyncOSPlaybackState();
            });

            UpdateMediaWidgetForTrack(newValue);
            _ = Task.Run(() => PreloadNextTrackAsync(token));
            return;
        }

        IsLoadingMedia = true;
        AcquireTransientWakeLock();

        try
        {
            _playbackService.Stop();
            CurrentPosition = TimeSpan.Zero;
            CurrentPositionSeconds = 0;
            SyncOSPlaybackState();

            // ==========================================
            // NEW CACHE LOGIC: Check Local Cache First
            // ==========================================
            ResolvedMedia? resolvedMedia = null;
            bool isCached = false;

            if (!string.IsNullOrEmpty(newValue.Url))
            {
                string? cachedPath = _trackCacheService.GetCachedTrackPath(newValue.Url);
                if (cachedPath != null)
                {
                    Debug.WriteLine($"[Playback] Using Local Cache for: {newValue.Url}");
                    resolvedMedia = new ResolvedMedia(cachedPath, "");
                    isCached = true;
                }
                else
                {
                    Stopwatch resolveSw = Stopwatch.StartNew();
                    resolvedMedia = await _youtubeResolver.ResolveMediaSourceAsync(newValue.Url);
                    resolveSw.Stop();
                    Debug.WriteLine($"[Playback] Resolved via Internet in {resolveSw.ElapsedMilliseconds}ms");
                }
            }

            if (token.IsCancellationRequested || resolvedMedia == null || string.IsNullOrEmpty(resolvedMedia.Url))
            {
                // Show a user-facing error if the track couldn't be resolved
                // (e.g. embedding-disabled video without cookies, or YouTube bot check).
                if (!token.IsCancellationRequested && resolvedMedia == null)
                {
                    Debug.WriteLine("[Playback] Resolve returned null — showing error to user");
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        string cookieHint = Services.YouTubeResolverService.HasCookies
                            ? "even with your YouTube cookies.\n\nThis video may require:\n• A fresh YouTube login (re-export cookies)\n• SABR streaming support (not yet available)\n• The official YouTube app (ReVanced)"
                            : ".\n\nThis video may require YouTube account cookies.\nGo to Settings → Import YouTube Account.";

                        await ShowConfirmAsync(
                            "Playback Error",
                            "This video could not be loaded" + cookieHint,
                            "OK",
                            ModalBackgroundTapBehavior.Confirm,
                            isDanger: true);
                    });
                }

                ReleaseTransientWakeLock();
                IsLoadingMedia = false;
                return;
            }

            double absoluteRestore = _initialRestorePosition + (newValue.StartTime ?? 0);
            double targetStartMs = absoluteRestore > 0 ? absoluteRestore * 1000.0 : (newValue.StartTime ?? 0) * 1000.0;
            _initialRestorePosition = 0;

            _playbackStartupSw = Stopwatch.StartNew();
            _playbackService.Play(resolvedMedia.Url, resolvedMedia.UserAgent, targetStartMs);

            // ==========================================
            // BACKGROUND CACHE: Use YoutubeExplode to get a fresh stream URL.
            // YoutubeExplode 6.6.2 now uses VISIONOS (same as our resolver) but
            // may select a different itag or URL variant that downloads faster.
            // It also handles cipher deciphering for videos where VISIONOS returns
            // ciphered URLs instead of direct ones.
            // ==========================================
            if (!isCached && !resolvedMedia.Url.StartsWith("/") && !resolvedMedia.Url.StartsWith("file://"))
            {
                string cacheOriginalUrl = newValue.Url;
                string fallbackCacheUrl = resolvedMedia.Url; // use resolved URL as fallback

                _ = Task.Run(async () =>
                {
                    string cacheUrl = fallbackCacheUrl;

                    try
                    {
                        // Try YoutubeExplode first — it may get a different/faster URL
                        YoutubeExplode.YoutubeClient? yteClient = Services.YouTubeResolverService.HasCookies
                            ? Services.YouTubeResolverService.GetYoutubeClientWithCookies()
                            : _youtubeResolver.GetYoutubeExplodeClient();

                        if (yteClient != null)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            var manifest = await yteClient.Videos.Streams.GetManifestAsync(
                                YoutubeExplode.Videos.VideoId.Parse(cacheOriginalUrl), cts.Token);

                            var bestStream = manifest.GetAudioOnlyStreams()
                                .OrderByDescending(s => s.AudioCodec.Contains("opus") ? 3 :
                                                         s.AudioCodec.Contains("mp4a") ? 2 : 1)
                                .ThenByDescending(s => s.Bitrate)
                                .FirstOrDefault();

                            if (bestStream != null && !string.IsNullOrEmpty(bestStream.Url))
                            {
                                cacheUrl = bestStream.Url;
                                Debug.WriteLine($"[Cache] Using YoutubeExplode URL for caching (itag may differ from playback)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Cache] YoutubeExplode URL fetch failed, using VISIONOS URL: {ex.Message}");
                    }

                    await _trackCacheService.CacheTrackAsync(cacheOriginalUrl, cacheUrl);
                });
            }

            totalSw.Stop();
            Debug.WriteLine($"[Playback] ReloadMediaAsync completed in {totalSw.ElapsedMilliseconds}ms");

            _ = Task.Run(() => PreloadNextTrackAsync(token));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playback] ReloadMediaAsync failed: {ex.Message}");
            ReleaseTransientWakeLock();
        }
    }

    private void OnPlaybackTimeChanged(double timeMs)
    {
        double endMs = (CurrentTrack?.EndTime ?? 0) * 1000.0;
        if (endMs > 0 && timeMs >= endMs)
        {
            if (!IsLoadingMedia)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!IsLoadingMedia)
                    {
                        IsLoadingMedia = true;
                        PlayNextCommand.Execute("Natural");
                    }
                });
            }
            return;
        }

        long currentTicks = Environment.TickCount64;
        if (currentTicks - _lastTimeUpdateTicks < MinimumTimeUpdateIntervalMs)
            return;

        _lastTimeUpdateTicks = currentTicks;

        double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
        double relativeTimeMs = timeMs - startMs;
        if (relativeTimeMs < 0) relativeTimeMs = 0;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPosition = TimeSpan.FromMilliseconds(relativeTimeMs);
            if (!_isDragging)
                CurrentPositionSeconds = CurrentPosition.TotalSeconds;

            SyncActiveLyric(CurrentPosition);
        });
    }

    private void UpdateTrackDuration(double totalMediaSeconds, PlaylistItem? track)
    {
        double end = track?.EndTime ?? totalMediaSeconds;
        double start = track?.StartTime ?? 0;
        double duration = end - start;

        if (duration <= 0)
            duration = totalMediaSeconds;

        Duration = TimeSpan.FromSeconds(duration);
        DurationSeconds = duration;

        if (track != null)
        {
            bool changed = track.Duration != Duration;
            track.Duration = Duration;

            if (changed)
                RequestSaveLibrary();
        }
    }

    private void UpdateMediaWidgetForTrack(PlaylistItem? track)
    {
        if (track == null) return;

        string widgetIconUrl = string.Empty;
        Playlist? pl = track.Playlist;
        PlaylistIconType iconType = pl?.IconType ?? PlaylistIconType.Playlist;

        if (iconType == PlaylistIconType.Track)
        {
            widgetIconUrl = !string.IsNullOrEmpty(track.IconUrl) ? track.IconUrl : pl?.IconUrl ?? string.Empty;
        }
        else if (iconType == PlaylistIconType.YouTube)
        {
            string? ytThumb = null;

            if (MediaUrlHelper.IsYouTubeUrl(track.Url))
            {
                YoutubeExplode.Videos.VideoId? videoId = YoutubeExplode.Videos.VideoId.TryParse(track.Url);
                if (videoId != null)
                {
                    ytThumb = $"https://img.youtube.com/vi/{videoId.Value}/hqdefault.jpg";
                }
            }

            widgetIconUrl = !string.IsNullOrEmpty(ytThumb) ? ytThumb : pl?.IconUrl ?? track.IconUrl ?? string.Empty;
        }
        else
        {
            widgetIconUrl = !string.IsNullOrEmpty(pl?.IconUrl) ? pl.IconUrl : track.IconUrl ?? string.Empty;
        }

        _ = UpdateOSMediaWidgetsAsync(track.Title, pl?.Name ?? "Musicthingy", widgetIconUrl);
    }

    private async Task ResolveSingleTrackDurationAsync(PlaylistItem track, CancellationToken cancellationToken)
    {
        bool needsSave = await ResolveSingleTrackDurationInnerAsync(track, cancellationToken);
        if (needsSave)
        {
            RequestSaveLibrary();
        }
    }

    private async Task<bool> ResolveSingleTrackDurationInnerAsync(PlaylistItem track, CancellationToken cancellationToken)
    {
        try
        {
            bool isSuccess = false;

            if (track.EndTime.HasValue)
            {
                double end = track.EndTime.Value;
                double start = track.StartTime ?? 0;
                if (end > start)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (CurrentTrack == track)
                            UpdateTrackDuration(end, track);
                        else
                            track.Duration = TimeSpan.FromSeconds(end - start);
                    });
                    isSuccess = true;
                }
            }

            if (!isSuccess && MediaUrlHelper.IsYouTubeUrl(track.Url))
            {
                TimeSpan? duration = await _youtubeResolver.GetVideoDurationAsync(track.Url, cancellationToken);
                if (duration.HasValue)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (CurrentTrack == track)
                            UpdateTrackDuration(duration.Value.TotalSeconds, track);
                        else
                        {
                            double start = track.StartTime ?? 0;
                            double durationSecs = duration.Value.TotalSeconds - start;
                            track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                        }
                    });
                    isSuccess = true;
                }
            }
            else if (!isSuccess)
            {
                TimeSpan? duration = await _mediaProbingService.ProbeMediaDurationAsync(track.Url, cancellationToken);
                if (duration.HasValue)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (CurrentTrack == track)
                            UpdateTrackDuration(duration.Value.TotalSeconds, track);
                        else
                        {
                            double start = track.StartTime ?? 0;
                            double durationSecs = duration.Value.TotalSeconds - start;
                            track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                        }
                    });
                    isSuccess = true;
                }
            }

            if (isSuccess)
            {
                track.IsResolutionAttempted = true;
                MainThread.BeginInvokeOnMainThread(() => track.Playlist?.RequestRecalculateDuration());
                return true;
            }
            else
            {
                track.IsResolutionAttempted = true;
                return false;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Resolve] Error resolving duration for {track.Url}: {ex.Message}");
            return false;
        }
    }

    private async Task ResolvePlaylistDurationsAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        if (playlist == null)
            return;

        List<PlaylistItem> itemsToResolve = new();

        if (MainThread.IsMainThread)
        {
            itemsToResolve = playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList();
        }
        else
        {
            TaskCompletionSource<List<PlaylistItem>> tcs = new TaskCompletionSource<List<PlaylistItem>>();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { tcs.SetResult(playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList()); }
                catch { tcs.SetResult(new()); }
            });
            itemsToResolve = await tcs.Task;
        }

        if (!itemsToResolve.Any())
            return;

        await Task.Run(async () =>
        {
            int needsSave = 0;

            IEnumerable<Task> tasks = itemsToResolve.Select(async track =>
            {
                await _probeConcurrencyLimit.WaitAsync(cancellationToken);

                try
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    bool isSuccess = await ResolveSingleTrackDurationInnerAsync(track, cancellationToken);
                    if (isSuccess)
                    {
                        Interlocked.Exchange(ref needsSave, 1);
                    }
                }
                finally { _probeConcurrencyLimit.Release(); }
            });

            await Task.WhenAll(tasks);

            if (needsSave == 1)
                RequestSaveLibrary();
        });
    }
}