using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        _dataService = new PlaylistDataService();
#if DEBUG
        LoadDummyData();
#endif
#if WINDOWS
        // Clean up old cached video files in the background on startup
        Task.Run(CleanupCache);
#endif
        // Listen for OS Media Key presses (Next/Previous) from Windows and Android
        WeakReferenceMessenger.Default.Register<Models.MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Next" && PlayNextCommand.CanExecute(null))
                PlayNextCommand.Execute(null);
            else if (m.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
                PlayPreviousCommand.Execute(null);
        });
    }

    private void CleanupCache()
    {
        try
        {
            var cacheDir = FileSystem.CacheDirectory;
            var files = Directory.GetFiles(cacheDir, "*.mp4");
            foreach (var file in files)
            {
                // Delete cache files older than 3 days to save disk space
                if (File.GetCreationTime(file) < DateTime.Now.AddDays(-3))
                {
                    File.Delete(file);
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    public void PlayPrevious()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = PlaybackQueue.Count - 1;
        CurrentTrack = PlaybackQueue[prevIndex];
    }

    private readonly YoutubeClient _youtubeClient = new();
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _resolvedCache = new();
    private readonly PlaylistDataService _dataService;

    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new();

    [ObservableProperty]
    public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotMediaSelected))]
    public partial PlaylistItem? CurrentTrack { get; set; }

    public bool IsMediaSelected => CurrentTrack != null;
    public bool IsNotMediaSelected => CurrentTrack == null;

    [ObservableProperty]
    public partial MediaSource? CurrentMediaSource { get; set; }

    // --- TIME TRACKING & ENFORCING END TIME ---
    [ObservableProperty]
    public partial TimeSpan CurrentPosition { get; set; }

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    private PlaylistItem? _lastTrackSkipped;
    private bool _hasPerformedInitialSeek;

    partial void OnDurationChanged(TimeSpan value)
    {
        // Now that the media file is cached locally (or loaded fast on Android), Duration loads instantly.
        // Once the metadata is loaded, we can safely Seek and then Play.
        if (!_hasPerformedInitialSeek && CurrentTrack != null && value.TotalSeconds > 0)
        {
            _hasPerformedInitialSeek = true;

            double startPos = CurrentTrack.StartTime ?? 0;

            WeakReferenceMessenger.Default.Send(new MediaControlMessage
            {
                Action = "StartPlayback",
                Position = TimeSpan.FromSeconds(startPos)
            });
        }
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        if (CurrentTrack?.EndTime.HasValue == true && _lastTrackSkipped != CurrentTrack)
        {
            if (value.TotalSeconds >= CurrentTrack.EndTime.Value)
            {
                _lastTrackSkipped = CurrentTrack;
                MainThread.BeginInvokeOnMainThread(() => PlayNextCommand.Execute(null));
            }
        }
    }

    // --- THE BACKGROUND PLAYBACK QUEUE ---
    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    private PlaylistItem? _prefetchedTrack;
    private MediaSource? _prefetchedMediaSource;

    [RelayCommand]
    public void PlayNext()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        CurrentTrack = PlaybackQueue[nextIndex];
    }

    private async Task WarmPlaylistCacheAsync()
    {
#if ANDROID
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        await Parallel.ForEachAsync(PlaybackQueue, options, async (item, token) =>
        {
            await ResolveMediaSourceAsync(item.Url);
        });
#else
        // Do not pre-download the entire playlist to disk on Windows.
        // Background prefetching of the *next* track handles this perfectly.
        await Task.CompletedTask;
#endif
    }

    partial void OnCurrentTrackChanged(PlaylistItem? oldValue, PlaylistItem? newValue)
    {
        _lastTrackSkipped = null;
        _hasPerformedInitialSeek = false;

        if (newValue == null)
        {
            CurrentMediaSource = null;
            WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });
            return;
        }

        if (SelectedPlaylist != null && !PlaybackQueue.Contains(newValue))
        {
            PlaybackQueue.Clear();
            foreach (var item in SelectedPlaylist.Items)
                PlaybackQueue.Add(item);

            _ = WarmPlaylistCacheAsync();
        }

        // OPTIMIZATION: Same-URL Seamless Transition
        if (oldValue != null && oldValue.Url == newValue.Url && CurrentMediaSource != null)
        {
            double newStart = newValue.StartTime ?? 0;
            bool isContinuous = Math.Abs(CurrentPosition.TotalSeconds - newStart) <= 10;

            if (isContinuous)
            {
                _hasPerformedInitialSeek = true; // Natural boundary cross, keep playing seamlessly
            }
            else
            {
                _hasPerformedInitialSeek = true;
                // Because we cache files locally on Windows, we NO LONGER have to reload the media source
                // to avoid the network throttle. We can just send a fast Seek command directly!
                WeakReferenceMessenger.Default.Send(new MediaControlMessage
                {
                    Action = "StartPlayback",
                    Position = TimeSpan.FromSeconds(newStart)
                });
            }

            _ = PrefetchNextTrackAsync();
            return;
        }

        WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });
        CurrentMediaSource = null;

        _ = ReloadMediaAsync(newValue);
        _ = PrefetchNextTrackAsync();
    }

    private async Task ReloadMediaAsync(PlaylistItem newValue)
    {
        await Task.Delay(50);
        if (CurrentTrack != newValue) return;

        if (_prefetchedTrack == newValue && _prefetchedMediaSource != null)
        {
            CurrentMediaSource = _prefetchedMediaSource;
        }
        else
        {
            CurrentMediaSource = await ResolveMediaSourceAsync(newValue.Url);
        }
    }

    private async Task PrefetchNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;
        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        if (_prefetchedTrack == nextTrack) return;

        _prefetchedTrack = nextTrack;
        _prefetchedMediaSource = await ResolveMediaSourceAsync(nextTrack.Url);
    }

    // Returns a highly-optimized MediaSource
    private async Task<MediaSource?> ResolveMediaSourceAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                var videoId = YoutubeExplode.Videos.VideoId.Parse(url);

#if ANDROID
                if (_resolvedCache.TryGetValue(url, out var cached) && DateTime.Now < cached.Expiry)
                    return MediaSource.FromUri(cached.Url);

                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                var streamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                if (streamInfo != null)
                {
                    _resolvedCache[url] = (streamInfo.Url, DateTime.Now.AddHours(5));
                    return MediaSource.FromUri(streamInfo.Url);
                }
#else
                // WINDOWS OPTIMIZATION: Bypasses the 50kbps YouTube throttle natively
                string cacheDir = FileSystem.CacheDirectory;
                string tempFilePath = Path.Combine(cacheDir, $"{videoId.Value}.mp4");

                // Check if we already downloaded this video (e.g. earlier in the Hollow Knight playlist)
                if (!File.Exists(tempFilePath))
                {
                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
                    var streamInfo = manifest.GetAudioOnlyStreams()
                        .Where(s => s.Container == Container.Mp4)
                        .GetWithHighestBitrate();

                    if (streamInfo != null)
                    {
                        // YoutubeExplode's internal HttpClient bypasses YouTube's throttle, downloading 
                        // a full 1-hour audio file in roughly 1-3 seconds.
                        await _youtubeClient.Videos.Streams.DownloadAsync(streamInfo, tempFilePath);
                    }
                }

                if (File.Exists(tempFilePath))
                {
                    // Playing from a local file makes WMF seeking genuinely 0ms latency.
                    return MediaSource.FromFile(tempFilePath);
                }
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                return null;
            }
        }

        return MediaSource.FromUri(url);
    }

    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}", CreatedAt = DateTime.Now };
        Playlists.Add(newList);
        SelectedPlaylist = newList;
    }

    private static readonly string[] androidFileTypeJSON = ["application/json"];
    private static readonly string[] windowsFileTypeJSON = [".json"];

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, windowsFileTypeJSON },
                    { DevicePlatform.Android, androidFileTypeJSON },
                });

            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Playlist JSON file",
                FileTypes = customFileType
            });

            if (result != null)
            {
                using var stream = await result.OpenReadAsync();
                using var reader = new StreamReader(stream);
                var jsonContent = await reader.ReadToEndAsync();

                var importedPlaylist = await _dataService.ImportPlaylistFromJsonAsync(jsonContent);

                if (importedPlaylist != null)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Playlists.Add(importedPlaylist);
                        SelectedPlaylist = importedPlaylist;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", $"Failed to import: {ex.Message}", "OK");
        }
    }

    private void LoadDummyData()
    {
        Playlist dummyList = new()
        {
            Name = "Test",
            CreatedAt = DateTime.Now
        };

        dummyList.Items.Add(new PlaylistItem { Title = "Skarrsinger Karmelita", Url = "https://www.youtube.com/watch?v=dhc4WFEeZfw&list=RDdhc4WFEeZfw&start_radio=1", StartTime = 5466, EndTime = 5573 });
        dummyList.Items.Add(new PlaylistItem { Title = "76", Url = "https://static.bandlab.com/revisions-formatted/7277ea00-254d-9b8c-0eab-c6b7bf94e414/7277ea00-254d-9b8c-0eab-c6b7bf94e414.m4a" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Empty", CreatedAt = DateTime.Now });

        SelectedPlaylist = dummyList;
    }
}