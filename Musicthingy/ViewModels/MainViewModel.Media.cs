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

    private void InitializeVLCEngine()
    {
        if (_libVLC != null) return;

        _libVLC = new LibVLC("--no-video");
        _player = new MediaPlayer(_libVLC);

        _player.TimeChanged += (s, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPosition = TimeSpan.FromMilliseconds(e.Time);
            // Only update the visual slider position if the user ISN'T currently dragging it
            if (!_isDragging)
            {
                CurrentPositionSeconds = CurrentPosition.TotalSeconds;
            }
        });

        _player.LengthChanged += (s, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            Duration = TimeSpan.FromMilliseconds(e.Length);
            DurationSeconds = Duration.TotalSeconds;
        });

        _player.Playing += (s, e) => MainThread.BeginInvokeOnMainThread(() => { IsPlaying = true; IsLoadingMedia = false; });
        _player.Paused += (s, e) => MainThread.BeginInvokeOnMainThread(() => IsPlaying = false);

        _player.EndReached += (s, e) =>
        {
            Task.Run(() => MainThread.BeginInvokeOnMainThread(() => PlayNextCommand.Execute(null)));
        };
    }

    private async Task<string?> ResolveMediaSourceAsync(PlaylistItem track)
    {
        if (track == null || string.IsNullOrEmpty(track.Url)) return null;

        if (track.Url.Contains("youtube.com") || track.Url.Contains("youtu.be"))
        {
            try
            {
                // FORCE DNS AND HTTP REQUESTS OFF THE MAIN THREAD FOR ANDROID!
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
        InitializeVLCEngine();

        IsLoadingMedia = true;
        _player!.Stop();

        string? rawAudioUrl = await ResolveMediaSourceAsync(newValue);
        if (rawAudioUrl == null)
        {
            IsLoadingMedia = false;
            return;
        }

        var media = new Media(_libVLC!, new Uri(rawAudioUrl));

        if (newValue.StartTime.HasValue) media.AddOption($":start-time={newValue.StartTime.Value}");
        if (newValue.EndTime.HasValue) media.AddOption($":stop-time={newValue.EndTime.Value}");

        if (_initialRestorePosition > 0)
        {
            media.AddOption($":start-time={_initialRestorePosition}");
            _initialRestorePosition = 0;
        }

        _player.Play(media);

        UpdateOSMediaWidgets(newValue.Title, SelectedPlaylist?.Name ?? "Musicthingy", newValue.IconUrl, true);
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