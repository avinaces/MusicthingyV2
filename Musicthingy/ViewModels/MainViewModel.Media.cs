using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Diagnostics;
using System.Threading;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const int PreloadTrackDelayMs = 15000;
    private const int MinimumTimeUpdateIntervalMs = 500;
    private const int ParseDelayTimeoutMs = 5000;
    private const int MaxSeekDesyncToleranceMs = 1500;

    // These services should be injected into the MainViewModel constructor via DI.
    // e.g. public MainViewModel(YouTubeResolverService youtubeResolver, IMediaPlaybackService playbackService) { ... }
    private readonly YouTubeResolverService _youtubeResolver;
    private readonly IMediaPlaybackService _playbackService;

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
            IsPlaying = true;
            IsLoadingMedia = false;
            SyncOSPlaybackState();
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
        AcquireTransientWakeLock();
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
        _mediaLoadCts?.Cancel();
        _mediaLoadCts = new CancellationTokenSource();
        CancellationToken token = _mediaLoadCts.Token;

        Stopwatch totalSw = Stopwatch.StartNew();

        Debug.WriteLine($"[Playback] ===== START LOAD =====");
        Debug.WriteLine($"[Playback] Track: {newValue.Title} (Start: {newValue.StartTime}, End: {newValue.EndTime})");

        if (IsAutoFollowEnabled)
            TriggerAutoFollow();

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

                double lengthSecs = _playbackService.LengthMs > 0 ? (_playbackService.LengthMs / 1000.0) : (CurrentTrack?.Duration.TotalSeconds ?? 0);
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

            Stopwatch resolveSw = Stopwatch.StartNew();
            ResolvedMedia? resolvedMedia = string.IsNullOrEmpty(newValue.Url)
                ? null
                : await _youtubeResolver.ResolveMediaSourceAsync(newValue.Url);
            resolveSw.Stop();

            Debug.WriteLine($"[Playback] Resolved media source in {resolveSw.ElapsedMilliseconds}ms");

            if (token.IsCancellationRequested || resolvedMedia == null || string.IsNullOrEmpty(resolvedMedia.Url))
            {
                ReleaseTransientWakeLock();
                return;
            }

            double absoluteRestore = _initialRestorePosition + (newValue.StartTime ?? 0);
            double targetStartMs = absoluteRestore > 0
                ? absoluteRestore * 1000.0
                : (newValue.StartTime ?? 0) * 1000.0;

            _initialRestorePosition = 0;
            _playbackStartupSw = Stopwatch.StartNew();

            _playbackService.Play(resolvedMedia.Url, resolvedMedia.UserAgent, targetStartMs);

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
            bool needsSave = false;

#if WINDOWS
            if (_probeVlc == null)
                _probeVlc = new LibVLCSharp.Shared.LibVLC("--no-video", "--no-audio", "--network-caching=500");
#endif

            IEnumerable<Task> tasks = itemsToResolve.Select(async track =>
            {
                await _probeConcurrencyLimit.WaitAsync(cancellationToken);

                try
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

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

                    if (!isSuccess && MediaUrlHelper.IsYouTubeUrl(track.Url))
                    {
                        TimeSpan? duration = await _youtubeResolver.GetVideoDurationAsync(track.Url, cancellationToken);

                        if (duration.HasValue)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                double start = track.StartTime ?? 0;
                                double durationSecs = duration.Value.TotalSeconds - start;
                                track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                            });
                            isSuccess = true;
                        }
                    }
                    else if (!isSuccess)
                    {
                        TimeSpan? duration = await ProbeMediaDurationAsync(track.Url, cancellationToken);

                        if (duration.HasValue)
                        {
                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                double start = track.StartTime ?? 0;
                                double durationSecs = duration.Value.TotalSeconds - start;
                                track.Duration = TimeSpan.FromSeconds(Math.Max(durationSecs, 0));
                            });
                            isSuccess = true;
                        }
                    }

                    if (isSuccess)
                    {
                        track.IsResolutionAttempted = true;
                        needsSave = true;
                        MainThread.BeginInvokeOnMainThread(() => playlist.RequestRecalculateDuration());
                    }
                    else
                    {
                        // Mark attempted so we don't infinitely retry broken links on every selection
                        track.IsResolutionAttempted = true;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Debug.WriteLine($"[Resolve] Error resolving duration for {track.Url}: {ex.Message}"); }
                finally { _probeConcurrencyLimit.Release(); }
            });

            await Task.WhenAll(tasks);

            if (needsSave)
                RequestSaveLibrary();
        });
    }

    private async Task<TimeSpan?> ProbeMediaDurationAsync(string url, CancellationToken cancellationToken)
    {
#if WINDOWS
        try
        {
            if (_probeVlc == null) return null;

            using var media = new LibVLCSharp.Shared.Media(_probeVlc, new Uri(url));
            var parseTask = media.Parse(LibVLCSharp.Shared.MediaParseOptions.ParseNetwork);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(ParseDelayTimeoutMs, delayCts.Token);

            var completedTask = await Task.WhenAny(parseTask, delayTask);

            if (completedTask == parseTask)
            {
                delayCts.Cancel();
                if (media.Duration > 0) return TimeSpan.FromMilliseconds(media.Duration);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Probe Windows] LibVLC Error: {ex.Message}");
        }
        return null;

#elif ANDROID
        var tcs = new TaskCompletionSource<TimeSpan?>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Cancel via token aborts the completion source
        cts.Token.Register(() => tcs.TrySetResult(null));

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context).Build();
                var listener = new ProbePlayerListener(player, tcs);
                player.AddListener(listener);

                var mediaItem = AndroidX.Media3.Common.MediaItem.FromUri(global::Android.Net.Uri.Parse(url)!);
                player.SetMediaItem(mediaItem);
                player.Prepare(); // Run a headless prep to read the metadata
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Probe Android] ExoPlayer Init Error: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        // Add a safety hard-timeout fallback in case ExoPlayer gets stuck buffering forever
        _ = Task.Delay(ParseDelayTimeoutMs, cts.Token).ContinueWith(t => tcs.TrySetResult(null));

        return await tcs.Task;
#else
        return await Task.FromResult<TimeSpan?>(null);
#endif
    }

#if ANDROID
    // Standard Object/Listener required for native ExoPlayer bindings
    private class ProbePlayerListener : Java.Lang.Object, AndroidX.Media3.Common.IPlayerListener
    {
        private readonly AndroidX.Media3.ExoPlayer.IExoPlayer _player;
        private readonly TaskCompletionSource<TimeSpan?> _tcs;
        private bool _isReleased;

        public ProbePlayerListener(AndroidX.Media3.ExoPlayer.IExoPlayer player, TaskCompletionSource<TimeSpan?> tcs)
        {
            _player = player;
            _tcs = tcs;
        }

        public void OnPlaybackStateChanged(int playbackState)
        {
            // 3 = StateReady, 4 = StateEnded, 1 = StateIdle
            if (playbackState == 3) // StateReady
            {
                var durationMs = _player.Duration;
                if (durationMs > 0 && durationMs != AndroidX.Media3.Common.C.TimeUnset)
                    _tcs.TrySetResult(TimeSpan.FromMilliseconds(durationMs));
                else
                    _tcs.TrySetResult(null);

                ReleasePlayer();
            }
            else if (playbackState == 4 || playbackState == 1) // Ended or Idle without Ready state
            {
                _tcs.TrySetResult(null);
                ReleasePlayer();
            }
        }

        public void OnPlayerError(AndroidX.Media3.Common.PlaybackException? error)
        {
            Debug.WriteLine($"[Probe Android] ExoPlayer Source Error: {error?.Message}");
            _tcs.TrySetResult(null);
            ReleasePlayer();
        }

        private void ReleasePlayer()
        {
            if (_isReleased) return;
            _isReleased = true;

            // Player MUST be released strictly on Main Thread to prevent JNI crashes
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    _player.RemoveListener(this);
                    _player.Release();
                    _player.Dispose();
                }
                catch { }
            });
        }
    }
#endif
}