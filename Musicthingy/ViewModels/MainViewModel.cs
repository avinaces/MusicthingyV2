using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static Action? _cleanupOldInstances;

    private readonly PlaylistDataService _dataService;

    [ObservableProperty]
    public partial bool IsLoadingMedia { get; set; }

    [ObservableProperty]
    public partial double MediaDownloadProgress { get; set; }

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

    public MainViewModel()
    {
        // Stop media and unhook any existing background instances to prevent multiple tracks playing
        _cleanupOldInstances?.Invoke();
        _cleanupOldInstances = CleanupMedia;

        _dataService = new PlaylistDataService();

        // Load your saved library asynchronously instead of just DummyData
        Task.Run(LoadStateAsync);

#if WINDOWS
        // Clean up old cached video files in the background on startup
        Task.Run(CleanupCache);
#endif

        // Listen for OS Media Key presses (Next/Previous) from Windows and Android
        WeakReferenceMessenger.Default.Register<MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Next" && PlayNextCommand.CanExecute(null))
                PlayNextCommand.Execute(null);
            else if (m.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
                PlayPreviousCommand.Execute(null);
        });
    }

    // In MainViewModel.cs

    private void CleanupMedia()
    {
        // 1. Unregister first so this instance stops listening to messages
        WeakReferenceMessenger.Default.Unregister<MediaControlMessage>(this);

        // 2. Tell the background player to stop
        WeakReferenceMessenger.Default.Send(new MediaControlMessage { Action = "Stop" });

        CurrentMediaSource = null;
    }

    partial void OnSelectedPlaylistChanged(Playlist? oldValue, Playlist? newValue)
    {
        if (!_isRestoringState)
        {
            Preferences.Default.Set("SelectedPlaylistName", newValue?.Name ?? string.Empty);
        }

        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }
}