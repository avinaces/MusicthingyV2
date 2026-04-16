using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Models;
using Musicthingy.Services;
using Microsoft.Maui.Controls;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.Messaging;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{


    public MainViewModel()
    {
        _dataService = new PlaylistDataService();
        LoadDummyData();

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

    [ObservableProperty]
    public partial ObservableCollection<Playlist> Playlists { get; set; } = new(); [ObservableProperty]
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

    partial void OnCurrentTrackChanged(PlaylistItem? value)
    {
        if (value == null)
        {
            CurrentMediaSource = null;
            return;
        }

        // If the user clicks a track directly from the UI that isn't in our background queue,
        // we assume they are starting a new playlist, so we copy the UI list to the background queue.
        if (SelectedPlaylist != null && !PlaybackQueue.Contains(value))
        {
            PlaybackQueue.Clear();
            foreach (var item in SelectedPlaylist.Items)
            {
                PlaybackQueue.Add(item);
            }
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

        if (!string.IsNullOrEmpty(rawUrl))
        {
            MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = MediaSource.FromUri(rawUrl));
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = null);
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

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                return await Task.Run(async () =>
                {
                    var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(url);
                    var streamInfo = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    return streamInfo?.Url;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                return null;
            }
        }

        // If it's a direct mp3/mp4, just return the URL immediately
        return url;
    }

    private async Task LoadMediaUrlAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = null);
            return;
        }

        bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

        if (isYouTube)
        {
            try
            {
                // Force the network request onto a background thread using Task.Run
                // This prevents Android.OS.NetworkOnMainThreadException
                await Task.Run(async () =>
                {
                    var youtube = new YoutubeClient();

                    // Fetch manifest in the background
                    var manifest = await youtube.Videos.Streams.GetManifestAsync(url);
                    var streamInfo = manifest.GetMuxedStreams().GetWithHighestVideoQuality();

                    if (streamInfo != null)
                    {
                        // Safely push the result back to the UI thread
                        MainThread.BeginInvokeOnMainThread(() =>
                            CurrentMediaSource = MediaSource.FromUri(streamInfo.Url));
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse YouTube stream: {ex.Message}");
                MainThread.BeginInvokeOnMainThread(() => CurrentMediaSource = null);
            }
        }
        else
        {
            // Direct file
            MainThread.BeginInvokeOnMainThread(() =>
                CurrentMediaSource = MediaSource.FromUri(url));
        }
    }


    [RelayCommand]
    private void AddNewPlaylist()
    {
        var newList = new Playlist { Name = $"New Playlist {Playlists.Count + 1}" };
        Playlists.Add(newList);
        SelectedPlaylist = newList;
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        try
        {
            // Define platform-specific types for JSON
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".json" } },
                    { DevicePlatform.Android, new[] { "application/json" } },
                    { DevicePlatform.iOS, new[] { "public.json" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.json" } },
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
        var dummyList = new Playlist
        {
            Name = "Chill Vibes",
            CreatedAt = DateTime.Now
        };

        dummyList.Items.Add(new PlaylistItem { Title = "Lofi Hip Hop", Url = "https://youtube.com/watch?v=jfKfPfyJRdk" });
        dummyList.Items.Add(new PlaylistItem { Title = "Synthwave Mix", Url = "https://youtube.com/watch?v=MVPTGNGiI" });

        Playlists.Add(dummyList);
        Playlists.Add(new Playlist { Name = "Workout", CreatedAt = DateTime.Now });

        SelectedPlaylist = dummyList;
    }
}