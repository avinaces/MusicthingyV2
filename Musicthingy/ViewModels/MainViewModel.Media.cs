using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
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
        _pauseOnLoad = false;
        _mediaLoadCts?.Cancel();
        _mediaLoadCts = new CancellationTokenSource();
        CancellationToken token = _mediaLoadCts.Token;

        // (v5) Only the previous track's LOAD is cancelled here. Its background
        // CACHE download is deliberately NOT cancelled — TrackCacheService now
        // runs a persistent queue, so skimmed tracks still end up cached and the
        // next track's download no longer dies waiting on the single-flight gate.
        // (Playback race safety: the older load checks token.IsCancellationRequested
        // after its resolve await and bails out before it could call Play.)

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

        // ==========================================
        // NOTE: Spotify routing intentionally absent — the Spotify integration
        // files (MainViewModel.Spotify.cs, Services/Spotify/*, the
        // _spotifyConnector field) are excluded from this package. If you ever
        // merge them, re-add the Spotify branch here (before CanReuseMedia):
        //   if (!string.IsNullOrEmpty(newValue.Url) &&
        //       MediaUrlHelper.IsSpotifyUrl(newValue.Url))
        //   { await HandleSpotifyTrackAsync(newValue, token); return; }
        // ==========================================

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
                // v5: ONE cache again — TrackCacheService now contains the fast
                // engine too (same directory, same naming), so this single call
                // sees every cached file and the stats page/playlist agree with
                // playback. (The separate FastTrackDownloader cache is gone; its
                // old files are migrated at startup.)
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
                    resolvedMedia = await _youtubeResolver.ResolveMediaSourceAsync(newValue.Url, token);
                    resolveSw.Stop();
                    Debug.WriteLine($"[Playback] Resolved via Internet in {resolveSw.ElapsedMilliseconds}ms");
                }
            }

            if (token.IsCancellationRequested || resolvedMedia == null || string.IsNullOrEmpty(resolvedMedia.Url))
            {
                // Show a user-facing error built from the resolver's ACTUAL
                // per-client outcomes (the [Resolve] log) — not a guessed
                // list of possible causes.
                if (!token.IsCancellationRequested && resolvedMedia == null)
                {
                    Debug.WriteLine("[Playback] Resolve returned null — showing error to user");
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        // The raw resolve log only — what every client actually
                        // answered, the YoutubeExplode error and the [SABR]
                        // session lines — with a Copy button. No interpreted
                        // hints or conclusions.
                        string log = BuildResolveFailureLog(newValue.Url);
                        var popup = new Views.PlaybackErrorPopup(log);
                        Page? page = Application.Current?.Windows?.FirstOrDefault()?.Page;
                        if (page != null)
                            await page.ShowPopupAsync(popup);
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
            // BACKGROUND CACHE (v5 — one queue, one cache):
            // enqueue the current track AND (for YouTube) the next track in
            // the playback queue. TrackCacheService drains the queue
            // sequentially in the background; downloads survive track
            // switches (that is why skimmed tracks never got cached before).
            // YouTube tracks resolve through YoutubeExplode (n-deciphered,
            // full speed); NON-YouTube tracks cache from their resolved
            // direct URL (not throttled, also fast). Local files
            // (file://, content://, plain paths) are skipped — they are
            // already on disk.
            // ==========================================
            if (!isCached && !string.IsNullOrEmpty(newValue.Url) &&
                !newValue.Url.StartsWith("/") &&
                !newValue.Url.StartsWith("file://") &&
                !newValue.Url.StartsWith("content://"))
            {
                string cacheOriginalUrl = newValue.Url;
                bool isYouTubeTrack = MediaUrlHelper.IsYouTubeUrl(newValue.Url);

                string? fallbackCacheUrl =
                    !string.IsNullOrEmpty(resolvedMedia.Url) &&
                    !resolvedMedia.Url.StartsWith("/") &&
                    !resolvedMedia.Url.StartsWith("file://") &&
                    !resolvedMedia.Url.StartsWith("content://") &&
                    !resolvedMedia.Url.StartsWith("sabr://") // SABR sessions are not download URLs
                        ? resolvedMedia.Url : null;

                // YoutubeExplode client only makes sense for YouTube tracks.
                YoutubeExplode.YoutubeClient? yteClient = isYouTubeTrack
                    ? (Services.YouTubeResolverService.HasCookies
                        ? Services.YouTubeResolverService.GetYoutubeClientWithCookies()
                        : _youtubeResolver.GetYoutubeExplodeClient())
                    : null;

                if (isYouTubeTrack || !string.IsNullOrEmpty(fallbackCacheUrl))
                {
                    _trackCacheService.EnqueueFast(yteClient, cacheOriginalUrl, fallbackCacheUrl);

                    // Pre-cache the next YouTube track in the queue
                    // (InnerTune-style): the downloader resolves the stream
                    // itself, so this needs no extra resolver call.
                    // Best-effort; deduplicated and cached-skip safe.
                    if (PlaybackQueue != null && PlaybackQueue.Count > 0)
                    {
                        int curIndex = PlaybackQueue.IndexOf(newValue);
                        if (curIndex >= 0)
                        {
                            var nextTrack = PlaybackQueue[(curIndex + 1) % PlaybackQueue.Count];
                            if (!string.IsNullOrEmpty(nextTrack?.Url) &&
                                MediaUrlHelper.IsYouTubeUrl(nextTrack.Url))
                                _trackCacheService.EnqueueFast(yteClient, nextTrack.Url, null);
                        }
                    }
                }
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

    /// <summary>
    /// The raw resolve log for a failed YouTube playback: every per-client
    /// outcome exactly as the resolver recorded it, the YoutubeExplode error
    /// and the [SABR] session lines — nothing interpreted, nothing added.
    /// Shown (and copyable) in the playback error popup.
    /// </summary>
    private static string BuildResolveFailureLog(string trackUrl)
    {
        string? videoId = Helpers.MediaUrlHelper.GetYouTubeVideoId(trackUrl);
        var attempt = videoId != null
            ? Services.YouTubeResolverService.GetLastResolveAttempt(videoId)
            : null;

        if (attempt == null)
            return "No resolve log was recorded for this track.";

        var sb = new System.Text.StringBuilder();
        foreach (string outcome in attempt.Outcomes)
            sb.Append(outcome).Append('\n');
        if (!string.IsNullOrWhiteSpace(attempt.YoutubeExplodeError))
            sb.Append("YoutubeExplode: ").Append(attempt.YoutubeExplodeError).Append('\n');
        if (videoId != null)
        {
            foreach (string line in Services.Sabr.SabrStreamRegistry.GetRecentLog(videoId))
                sb.Append(line).Append('\n');
        }

        string log = sb.ToString().TrimEnd();
        return log.Length > 0 ? log : "No resolve log was recorded for this track.";
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