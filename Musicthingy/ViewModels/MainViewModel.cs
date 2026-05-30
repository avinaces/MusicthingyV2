using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;

namespace Musicthingy.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static Action? _cleanupOldInstances;

    private CancellationTokenSource? _playlistResolutionCts;

    private readonly PlaylistDataService _dataService;

    [ObservableProperty] public partial bool IsLoadingMedia { get; set; }
    [ObservableProperty] public partial double MediaDownloadProgress { get; set; }
    [ObservableProperty] public partial ObservableCollection<Playlist> Playlists { get; set; } = new();
    [ObservableProperty] public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotMediaSelected))]
    public partial PlaylistItem? CurrentTrack { get; set; }

    public bool IsMediaSelected => CurrentTrack != null;
    public bool IsNotMediaSelected => CurrentTrack == null;

    public MainViewModel(
        ThemeManager themeManager,
        YouTubeResolverService youtubeResolver,
        IMediaPlaybackService playbackService)
    {
        _themeManager = themeManager;
        _youtubeResolver = youtubeResolver;
        _playbackService = playbackService;

        _cleanupOldInstances?.Invoke();
        _cleanupOldInstances = CleanupMedia;

        _dataService = new PlaylistDataService();

        // UPDATE: Call the method from MainViewModel.Media.cs to hook up the playback events!
        InitializePlaybackService();

        Task.Run(LoadStateAsync);

        // Restore Hardware Keyboard/Bluetooth support sent from MainActivity.cs
        WeakReferenceMessenger.Default.Register<MediaControlMessage>(this, (r, m) =>
        {
            if (m.Action == "Next" && PlayNextCommand.CanExecute(null))
                PlayNextCommand.Execute(null);
            else if (m.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
                PlayPreviousCommand.Execute(null);
            else if (m.Action == "PlayPause" && TogglePlayCommand.CanExecute(null))
                TogglePlayCommand.Execute(null);
        });
    }

    private void CleanupMedia()
    {
        WeakReferenceMessenger.Default.Unregister<MediaControlMessage>(this);

        // UPDATE: Swapped _player for _playbackService
        try { _playbackService.Stop(); } catch { }

        // Cancel any pending media load / preload tasks and dispose the CTS
        try
        {
            if (_mediaLoadCts != null)
            {
                _mediaLoadCts.Cancel();
                _mediaLoadCts.Dispose();
                _mediaLoadCts = null;
            }
        }
        catch { }

        // Unsubscribe playback event handlers to avoid retaining this VM through event refs
        try { UnsubscribePlaybackService(); } catch { }

        ReleaseTransientWakeLock();

#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(AndroidMediaService));
            intent.SetAction("STOP");
            context.StartService(intent);
        }
        catch { }
#endif
    }

    public void Dispose()
    {
        // Ensure media is cleaned up and subscriptions removed when the VM is disposed.
        try { CleanupMedia(); } catch { }
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedPlaylistChanged(Playlist? oldValue, Playlist? newValue)
    {
        if (!_isRestoringState)
        {
            Preferences.Default.Set("SelectedPlaylistName", newValue?.Name ?? string.Empty);
        }

        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;

            // Trigger fast-resolving missing tracks in the background
            _ = ResolvePlaylistDurationsAsync(newValue);
        }
    }
}