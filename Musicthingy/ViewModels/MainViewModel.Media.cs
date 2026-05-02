using LibVLCSharp.Shared;
using Musicthingy.Models;
using System.Diagnostics;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    private readonly YoutubeClient _youtubeClient = new();

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

            // FIX: Push UI-bound property updates to the Main Thread
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

            // FIX: Route to Main Thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Duration = TimeSpan.FromSeconds(duration);
                DurationSeconds = duration;
            });

            if (CurrentTrack != null)
            {
                string widgetIconUrl = !string.IsNullOrEmpty(SelectedPlaylist?.IconUrl)
                    ? SelectedPlaylist.IconUrl
                    : CurrentTrack.IconUrl;

                // Task.Run is fine here since OS APIs have their own thread marshalling
                _ = UpdateOSMediaWidgetsAsync(CurrentTrack.Title, SelectedPlaylist?.Name ?? "Musicthingy", widgetIconUrl);
            }
        };

        _player.Playing += (s, e) => {
            MainThread.BeginInvokeOnMainThread(() => {
                IsPlaying = true;
                IsLoadingMedia = false;
                SyncOSPlaybackState();
            });
            ReleaseTransientWakeLock();
        };

        _player.Paused += (s, e) => {
            MainThread.BeginInvokeOnMainThread(() => {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _player.Stopped += (s, e) => {
            MainThread.BeginInvokeOnMainThread(() => {
                IsPlaying = false;
                SyncOSPlaybackState();
            });
            if (!IsLoadingMedia) ReleaseTransientWakeLock();
        };

        _player.EncounteredError += (s, e) => {
            MainThread.BeginInvokeOnMainThread(() => {
                IsLoadingMedia = false;
            });
            ReleaseTransientWakeLock();
        };

        _player.EndReached += (s, e) =>
        {
            AcquireTransientWakeLock();

            // FIX: Do NOT use Task.Run for UI Commands! 
            // BeginInvokeOnMainThread escapes the LibVLC deadlock while keeping the CollectionView safe.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsLoadingMedia = true;
                PlayNextCommand.Execute(null);
            });
        };
    }

    private async Task<string?> ResolveMediaSourceAsync(PlaylistItem track)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;

        if (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be"))
        {
            try
            {
                return await Task.Run(async () =>
                {
                    var videoId = YoutubeExplode.Videos.VideoId.Parse(track.Url);
                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                    return manifest.GetAudioOnlyStreams().GetWithHighestBitrate()?.Url;
                });
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

        if (newValue.StartTime.HasValue) media.AddOption($":start-time={newValue.StartTime.Value}");
        if (newValue.EndTime.HasValue) media.AddOption($":stop-time={newValue.EndTime.Value}");

        if (_initialRestorePosition > 0)
        {
            double absoluteRestore = _initialRestorePosition + (newValue.StartTime ?? 0);
            media.AddOption($":start-time={absoluteRestore}");
            _initialRestorePosition = 0;
        }

        _player.Play(media);
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