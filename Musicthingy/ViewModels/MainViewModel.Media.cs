using LibVLCSharp.Shared;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Diagnostics;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private const int PreloadTrackDelayMs = 15000;
    private const int MinimumTimeUpdateIntervalMs = 500;
    private const int ParseDelayTimeoutMs = 5000;
    private const float FullCachePercentage = 100f;

    private static readonly SemaphoreSlim _vlcParseSemaphore = new(1, 1);

    // Drop-in instantiation of the new service, 
    // ideally inject this via constructor if DI is implemented for MainViewModel.
    private readonly YouTubeResolverService _youtubeResolver = new();

    private Stopwatch? _playbackStartupSw;
    private CancellationTokenSource? _mediaLoadCts;

    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private static LibVLC? _probeVlc;

    private long _lastTimeUpdateTicks = 0;


    private void InitializeVLCEngine()
    {
        if (_libVLC != null)
            return;

        _libVLC = new LibVLC(
            "--no-video",
            "--http-reconnect",
            "--verbose=2"
        );

        _player = new MediaPlayer(_libVLC);

        _player.Opening += (s, e) =>
        {
            Debug.WriteLine($"[VLC] Opening @ {_playbackStartupSw?.ElapsedMilliseconds}ms");
        };

        _player.Buffering += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                bool isBuffering = e.Cache < FullCachePercentage;
                if (IsLoadingMedia != isBuffering)
                {
                    IsLoadingMedia = isBuffering;
                }
            });
        };

        _player.Playing += (s, e) =>
        {
            Debug.WriteLine($"[VLC] Playing @ {_playbackStartupSw?.ElapsedMilliseconds}ms");

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

            if (!IsLoadingMedia)
                ReleaseTransientWakeLock();
        };

        _player.Stopped += (s, e) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsPlaying = false;
                SyncOSPlaybackState();
            });

            if (!IsLoadingMedia)
                ReleaseTransientWakeLock();
        };

        _player.EncounteredError += (s, e) =>
        {
            Debug.WriteLine($"[VLC] EncounteredError @ {_playbackStartupSw?.ElapsedMilliseconds}ms");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = false;
                PlayNextCommand.Execute("Error");
                Debug.WriteLine("Failed to play media");
            });

            ReleaseTransientWakeLock();
        };

        _player.EndReached += (s, e) =>
        {
            AcquireTransientWakeLock();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = true;
                PlayNextCommand.Execute("Natural");
            });
        };

        _player.TimeChanged += (s, e) =>
        {
            long currentTicks = Environment.TickCount64;

            if (currentTicks - _lastTimeUpdateTicks < MinimumTimeUpdateIntervalMs)
                return;

            _lastTimeUpdateTicks = currentTicks;

            double startMs = (CurrentTrack?.StartTime ?? 0) * 1000.0;
            double relativeTimeMs = e.Time - startMs;

            if (relativeTimeMs < 0)
                relativeTimeMs = 0;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                CurrentPosition = TimeSpan.FromMilliseconds(relativeTimeMs);

                if (!_isDragging)
                    CurrentPositionSeconds = CurrentPosition.TotalSeconds;

                SyncActiveLyric(CurrentPosition);
            });
        };

        _player.LengthChanged += (s, e) =>
        {
            Debug.WriteLine($"[VLC] LengthChanged={e.Length}ms @ {_playbackStartupSw?.ElapsedMilliseconds}ms");

            double totalSeconds = e.Length / 1000.0;
            double end = CurrentTrack?.EndTime ?? totalSeconds;
            double start = CurrentTrack?.StartTime ?? 0;
            double duration = end - start;

            if (duration <= 0)
                duration = totalSeconds;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Duration = TimeSpan.FromSeconds(duration);
                DurationSeconds = duration;

                if (CurrentTrack != null)
                {
                    bool changed = CurrentTrack.Duration != Duration;
                    CurrentTrack.Duration = Duration;

                    if (changed)
                        RequestSaveLibrary();
                }
            });

            if (CurrentTrack != null)
            {
                string widgetIconUrl = string.Empty;

                Playlist? pl = CurrentTrack.Playlist;
                PlaylistIconType iconType = pl?.IconType ?? PlaylistIconType.Playlist;

                if (iconType == PlaylistIconType.Track)
                {
                    widgetIconUrl = !string.IsNullOrEmpty(CurrentTrack.IconUrl)
                        ? CurrentTrack.IconUrl
                        : pl?.IconUrl ?? string.Empty;
                }
                else if (iconType == PlaylistIconType.YouTube)
                {
                    string? ytThumb = null;

                    if (!string.IsNullOrEmpty(CurrentTrack.Url) &&
                        (CurrentTrack.Url.Contains("youtube.com") || CurrentTrack.Url.Contains("youtu.be")))
                    {
                        YoutubeExplode.Videos.VideoId? videoId = YoutubeExplode.Videos.VideoId.TryParse(CurrentTrack.Url);

                        if (videoId != null)
                        {
                            ytThumb = $"https://img.youtube.com/vi/{videoId.Value}/hqdefault.jpg";
                        }
                    }

                    widgetIconUrl = !string.IsNullOrEmpty(ytThumb)
                        ? ytThumb
                        : pl?.IconUrl ?? CurrentTrack.IconUrl ?? string.Empty;
                }
                else
                {
                    widgetIconUrl = !string.IsNullOrEmpty(pl?.IconUrl)
                        ? pl.IconUrl
                        : CurrentTrack.IconUrl ?? string.Empty;
                }

                _ = UpdateOSMediaWidgetsAsync(
                    CurrentTrack.Title,
                    pl?.Name ?? "Musicthingy",
                    widgetIconUrl
                );
            }
        };
    }

    private async Task PreloadNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null)
            return;

        await Task.Delay(PreloadTrackDelayMs);

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1)
            return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        PlaylistItem nextTrack = PlaybackQueue[nextIndex];

        await ResolveMediaSourceAsync(nextTrack);
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

        IsLoadingMedia = true;
        AcquireTransientWakeLock();

        InitializeVLCEngine();

        _player!.Stop();

        CurrentPosition = TimeSpan.Zero;
        CurrentPositionSeconds = 0;
        SyncOSPlaybackState();

        Stopwatch resolveSw = Stopwatch.StartNew();
        string? rawAudioUrl = await ResolveMediaSourceAsync(newValue);
        resolveSw.Stop();

        Debug.WriteLine($"[Playback] Resolved media source in {resolveSw.ElapsedMilliseconds}ms");

        if (token.IsCancellationRequested || string.IsNullOrEmpty(rawAudioUrl))
        {
            ReleaseTransientWakeLock();
            return;
        }

        Media media = new Media(_libVLC!, new Uri(rawAudioUrl));

        media.AddOption(":network-caching=1000");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");

        if (newValue.StartTime.HasValue)
        {
            media.AddOption($":start-time={newValue.StartTime.Value}");
        }

        if (newValue.EndTime.HasValue)
        {
            media.AddOption($":stop-time={newValue.EndTime.Value}");
        }

        if (_initialRestorePosition > 0)
        {
            double absoluteRestore = _initialRestorePosition + (newValue.StartTime ?? 0);
            media.AddOption($":start-time={absoluteRestore}");
            _initialRestorePosition = 0;
        }

        _playbackStartupSw = Stopwatch.StartNew();
        _player.Play(media);

        totalSw.Stop();
        Debug.WriteLine($"[Playback] ReloadMediaAsync completed in {totalSw.ElapsedMilliseconds}ms");

        _ = Task.Run(PreloadNextTrackAsync);
    }

    private async Task<string?> ResolveMediaSourceAsync(PlaylistItem track)
    {
        if (track == null || string.IsNullOrEmpty(track.Url))
            return null;

        return await _youtubeResolver.ResolveMediaSourceAsync(track.Url);
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
    }
}