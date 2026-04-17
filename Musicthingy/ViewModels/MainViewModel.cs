using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Web;
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
        // Listen for OS Media Key presses (Next/Previous) from Windows and Android
        WeakReferenceMessenger.Default.Register<Models.MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Next" && PlayNextCommand.CanExecute(null))
                PlayNextCommand.Execute(null);
            else if (m.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
                PlayPreviousCommand.Execute(null);
        });
    }

    [RelayCommand]
    public void PlayPrevious()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        // Subtract 1 from index. If it drops below 0, wrap around to the end of the list.
        int prevIndex = currentIndex - 1;
        if (prevIndex < 0) prevIndex = PlaybackQueue.Count - 1;

        CurrentTrack = PlaybackQueue[prevIndex];
    }

    private readonly YoutubeClient _youtubeClient = new();

    // Cache resolved URLs to make repeat clicks instant.
    // Use ConcurrentDictionary to prevent thread-safety crashes when background fetching.
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _resolvedCache = new();

    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new();

    [ObservableProperty]
    public partial Playlist? SelectedPlaylist { get; set; }

    private readonly PlaylistDataService _dataService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotMediaSelected))]
    public partial PlaylistItem? CurrentTrack { get; set; }

    public bool IsMediaSelected => CurrentTrack != null;
    public bool IsNotMediaSelected => CurrentTrack == null;

    [ObservableProperty]
    public partial MediaSource? CurrentMediaSource { get; set; }

    // 1. THE BACKGROUND PLAYBACK QUEUE
    [ObservableProperty]
    public partial ObservableCollection<PlaylistItem> PlaybackQueue { get; set; } = new();

    // Variables for pre-fetching the next song
    private PlaylistItem? _prefetchedTrack;
    private string? _prefetchedMediaUrl;

    // 2. AUTO-PLAY & LOOP COMMAND
    [RelayCommand]
    public void PlayNext()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        // Add 1 to index. If it goes past the end of the list, % loops it back to 0.
        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;

        // This triggers OnCurrentTrackChanged automatically
        CurrentTrack = PlaybackQueue[nextIndex];
    }

    private async Task WarmPlaylistCacheAsync()
    {
        // Use Parallel with a concurrency limit. This keeps the loading incredibly fast 
        // (1-3 seconds) but prevents YouTube from rate-limiting you on massive playlists.
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        await Parallel.ForEachAsync(PlaybackQueue, options, async (item, token) =>
        {
            await ResolveMediaUrlAsync(item.Url);
        });
    }

    partial void OnCurrentTrackChanged(PlaylistItem? value)
    {

        WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });


        CurrentMediaSource = null;

        if (value == null)
        {
            return;
        }

        // If the user clicks a track directly from the UI that isn't in our background queue,
        // we assume they are starting a new playlist, so we copy the UI list to the background queue.
        if (SelectedPlaylist != null && !PlaybackQueue.Contains(value))
        {
            PlaybackQueue.Clear();

            foreach (var item in SelectedPlaylist.Items)
                PlaybackQueue.Add(item);

            _ = WarmPlaylistCacheAsync();
        }

        // 3. APPLY PRE-FETCHED VIDEO (Skips the 10-second wait!)
        if (_prefetchedTrack == value && !string.IsNullOrEmpty(_prefetchedMediaUrl))
        {
            CurrentMediaSource = MediaSource.FromUri(_prefetchedMediaUrl);
        }
        else
        {
            // Fallback: If not prefetched (e.g., user clicked a random track), load it normally
            _ = LoadMediaUrlAsync(value);
        }

        // Immediately start quietly downloading the next track's URL in the background
        _ = PrefetchNextTrackAsync();
    }



    private async Task LoadMediaUrlAsync(PlaylistItem track)
    {
        var rawUrl = await ResolveMediaUrlAsync(track.Url);

        // only update the UI if the user hasn't switched to a different track 
        // while we were waiting for YouTube to respond.
        if (CurrentTrack == track)
        {
            if (!string.IsNullOrEmpty(rawUrl))
            {
                MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = MediaSource.FromUri(rawUrl));
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = null);
            }
        }
    }

    private async Task PrefetchNextTrackAsync()
    {
        if (PlaybackQueue == null || PlaybackQueue.Count == 0 || CurrentTrack == null) return;

        int currentIndex = PlaybackQueue.IndexOf(CurrentTrack);
        if (currentIndex == -1) return;

        // Find what the next track will be
        int nextIndex = (currentIndex + 1) % PlaybackQueue.Count;
        var nextTrack = PlaybackQueue[nextIndex];

        // Skip if we already prefetched it
        if (_prefetchedTrack == nextTrack) return;

        _prefetchedTrack = nextTrack;

        // Resolve the YouTube URL in the background while the current song is still playing
        _prefetchedMediaUrl = await ResolveMediaUrlAsync(nextTrack.Url);
    }

    // Helper function that does the slow YoutubeExplode fetching off the main thread
    private async Task<string?> ResolveMediaUrlAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        // 1. Check Cache first
        if (_resolvedCache.TryGetValue(url, out var cached) && DateTime.Now < cached.Expiry)
        {
            return cached.Url;
        }

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(url);
                var audioStreams = manifest.GetAudioOnlyStreams();


#if ANDROID

                var streamInfo = audioStreams.GetWithHighestBitrate(); // Android is objectively better, because it supports WebM

#else
                //Filter for 'Mp4' container specifically.
                // Topic videos usually have WebM (Opus) and Mp4 (AAC). 
                // Windows WILL crash/skip on WebM. We must force Mp4.
                var streamInfo = manifest.GetAudioOnlyStreams()
                    .Where(s => s.Container == Container.Mp4) 
                    .GetWithHighestBitrate();
#endif


                if (streamInfo != null)
                {
                    _resolvedCache[url] = (streamInfo.Url, DateTime.Now.AddHours(5));
                    return streamInfo.Url;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                await Shell.Current.DisplayAlertAsync("Failed to parse YouTube stream: ", ex.Message, "OK");
                return null;
            }
        }

        return url;
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
            // Define platform-specific types for JSON
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
                    // Update UI on the main thread
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