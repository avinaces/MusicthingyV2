using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
        _dataService = new PlaylistDataService();

#if DEBUG
        LoadDummyData();
#endif
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

    partial void OnSelectedPlaylistChanged(Playlist? oldValue, Playlist? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }
}