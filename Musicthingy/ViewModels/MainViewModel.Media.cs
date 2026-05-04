using LibVLCSharp.Shared;
using Musicthingy.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string RawUrl, DateTime FetchedAt)> _streamUrlCache = new();

    private LibVLC? _libVLC;
    private MediaPlayer? _player;

    private long _lastTimeUpdateTicks = 0;


    private void InitializeVLCEngine()
    {
        if (_libVLC != null) return;

        _libVLC = new LibVLC("--no-video");
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
            });

            if (CurrentTrack != null)
            {
                string widgetIconUrl = !string.IsNullOrEmpty(CurrentTrack.Playlist?.IconUrl)
                    ? CurrentTrack.Playlist.IconUrl
                    : CurrentTrack.IconUrl;

                _ = UpdateOSMediaWidgetsAsync(CurrentTrack.Title, CurrentTrack.Playlist?.Name ?? "Musicthingy", widgetIconUrl);
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

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        // Silently resolve and cache the next track's URL in the background
        await ResolveMediaSourceAsync(nextTrack);
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
                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
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

    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        IsLoadingMedia = true; // Set this BEFORE Stop() is called
        AcquireTransientWakeLock(); // Keep CPU awake while fetching the youtube stream URL

        InitializeVLCEngine();

        _player!.Stop();

        CurrentPosition = TimeSpan.Zero;
        CurrentPositionSeconds = 0;
        SyncOSPlaybackState();

        string? rawAudioUrl = await ResolveMediaSourceAsync(newValue);
        if (rawAudioUrl == null)
        {
            IsLoadingMedia = false;
            ReleaseTransientWakeLock(); // Release lock if it failed
            return;
        }

        var media = new Media(_libVLC!, new Uri(rawAudioUrl));

        media.AddOption(":network-caching=1000");

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