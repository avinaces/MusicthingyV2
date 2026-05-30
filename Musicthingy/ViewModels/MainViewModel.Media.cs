using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Diagnostics;

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

    // Temporary initialization method to map the constructor injection in a partial class setup
    private void InitializePlaybackService()
    {
        _playbackService.BufferingChanged += (s, isBuffering) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (IsLoadingMedia != isBuffering)
                    IsLoadingMedia = isBuffering;
            });
        };

        _playbackService.Playing += (s, e) =>
        {
            Debug.WriteLine($"[Playback] Playing @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = true;
                IsLoadingMedia = false;
                SyncOSPlaybackState();
            });
        };

        _playbackService.Paused += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _playbackService.Stopped += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _playbackService.ErrorEncountered += (s, e) =>
        {
            Debug.WriteLine($"[Playback] EncounteredError @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = false;
                PlayNextCommand.Execute("Error");
            });
            ReleaseTransientWakeLock();
        };

        _playbackService.EndReached += (s, e) =>
        {
            AcquireTransientWakeLock();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = true;
                PlayNextCommand.Execute("Natural");
            });
        };

        _playbackService.TimeChanged += (s, timeMs) => OnPlaybackTimeChanged(timeMs);
        _playbackService.LengthChanged += (s, lengthMs) =>
        {
            Debug.WriteLine($"[Playback] LengthChanged={lengthMs}ms @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
            MainThread.BeginInvokeOnMainThread(() => UpdateTrackDuration(lengthMs / 1000.0, CurrentTrack));
            UpdateMediaWidgetForTrack(CurrentTrack);
        };
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

    private async Task PreloadNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null)
            return;

        await Task.Delay(PreloadTrackDelayMs);

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        PlaylistItem nextTrack = PlaybackQueue[nextIndex];

        if (!string.IsNullOrEmpty(nextTrack.Url))
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
            _ = Task.Run(PreloadNextTrackAsync);
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

            _ = Task.Run(PreloadNextTrackAsync);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Playback] ReloadMediaAsync failed: {ex.Message}");
            ReleaseTransientWakeLock();
        }
    }

    private async Task ResolvePlaylistDurationsAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {

        return; // <-- TEMP DISABLE TO AVOID YT HANGS WHILE I INVESTIGATE
#if false
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
            try
            {
                await _vlcParseSemaphore.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (_probeVlc == null)
                    _probeVlc = new LibVLC("--no-video", "--no-audio", "--network-caching=500");

                List<PlaylistItem> playlistItems = playlist.Items.Where(i => i.Duration.TotalSeconds <= 0 && !i.IsResolutionAttempted && !string.IsNullOrEmpty(i.Url)).ToList();

                if (!playlistItems.Any())
                    return;

                bool needsSave = false;
                SemaphoreSlim concurrencyLimit = new SemaphoreSlim(4, 4);

                IEnumerable<Task> tasks = playlistItems.Select(async track =>
                {
                    await concurrencyLimit.WaitAsync(cancellationToken);

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

                        if (!isSuccess && (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be")))
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
                            Media media = new Media(_probeVlc, new Uri(track.Url));
                            Task<MediaParsedStatus> parseTask = media.Parse(MediaParseOptions.ParseNetwork);

                            using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            Task delayTask = Task.Delay(ParseDelayTimeoutMs, delayCts.Token);

                            Task completedTask = await Task.WhenAny(parseTask, delayTask);

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

                if (needsSave)
                    RequestSaveLibrary();
            }
            finally
            {
                _vlcParseSemaphore.Release();
            }
        });
#endif
    }
}