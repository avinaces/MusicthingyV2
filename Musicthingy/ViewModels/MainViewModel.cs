using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Musicthingy.ViewModels;

public enum SortField
{
    Custom,
    Title,
    Duration,
    LastPlayed
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static Action? _cleanupOldInstances;

    private CancellationTokenSource? _playlistResolutionCts;

    private readonly PlaylistDataService _dataService;

    [ObservableProperty] public partial bool IsLoadingMedia { get; set; }
    [ObservableProperty] public partial double MediaDownloadProgress { get; set; }
    [ObservableProperty] public partial ObservableCollection<Playlist> Playlists { get; set; } = new();
    [ObservableProperty] public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty] public partial ObservableCollection<PlaylistItem> DisplayedItems { get; set; } = new();

    // ==========================================
    // UNIVERSAL SORTING LOGIC & UI HELPERS
    // ==========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortModeText))]
    [NotifyPropertyChangedFor(nameof(IsSortCustom))]
    [NotifyPropertyChangedFor(nameof(IsSortTitle))]
    [NotifyPropertyChangedFor(nameof(IsSortDuration))]
    [NotifyPropertyChangedFor(nameof(IsSortLastPlayed))]
    [NotifyPropertyChangedFor(nameof(IsDirectionToggleVisible))]
    public partial SortField CurrentSortField { get; set; } = SortField.Custom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionIcon))]
    public partial bool IsSortAscending { get; set; } = true;

    public string CurrentSortModeText => CurrentSortField switch
    {
        SortField.Title => "Title",
        SortField.Duration => "Duration",
        SortField.LastPlayed => "Last Played",
        _ => "Custom Order"
    };

    public bool IsSortCustom => CurrentSortField == SortField.Custom;
    public bool IsSortTitle => CurrentSortField == SortField.Title;
    public bool IsSortDuration => CurrentSortField == SortField.Duration;
    public bool IsSortLastPlayed => CurrentSortField == SortField.LastPlayed;

    // Show toggle arrow only when not in custom mode
    public bool IsDirectionToggleVisible => CurrentSortField != SortField.Custom;
    public string SortDirectionIcon => IsSortAscending ? "arrow_down_icon.png" : "arrow_up_icon.png";

    [RelayCommand]
    private void SetSortField(SortField field)
    {
        if (CurrentSortField != field)
        {
            CurrentSortField = field;

            // Set intuitive default directions when changing the sorting field
            if (field == SortField.LastPlayed)
                IsSortAscending = false; // Default to Descending (Most recent tracks at the top)
            else
                IsSortAscending = true; // Default to Ascending (A-Z, Shortest to Longest)
        }
        RefreshDisplayedItems();
    }

    [RelayCommand]
    private void ToggleSortDirection()
    {
        if (CurrentSortField == SortField.Custom) return;

        IsSortAscending = !IsSortAscending;
        RefreshDisplayedItems();
    }
    // ==========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaSelected))]
    [NotifyPropertyChangedFor(nameof(IsNotMediaSelected))]
    public partial PlaylistItem? CurrentTrack { get; set; }

    public bool IsMediaSelected => CurrentTrack != null;
    public bool IsNotMediaSelected => CurrentTrack == null;

    public MainViewModel(
        ThemeManager themeManager,
        YouTubeResolverService youtubeResolver,
        IMediaPlaybackService playbackService,
        IMediaProbingService mediaProbingService)
    {
        _themeManager = themeManager;
        _youtubeResolver = youtubeResolver;
        _playbackService = playbackService;
        _mediaProbingService = mediaProbingService;

        _cleanupOldInstances?.Invoke();
        _cleanupOldInstances = CleanupMedia;

        _dataService = new PlaylistDataService();

        InitializePlaybackService();

        Task.Run(LoadStateAsync);

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

        try { _playbackService.Stop(); } catch { }

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
        try { CleanupMedia(); } catch { }
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedPlaylistChanged(Playlist? oldValue, Playlist? newValue)
    {
        if (!_isRestoringState)
        {
            Preferences.Default.Set("SelectedPlaylistName", newValue?.Name ?? string.Empty);
        }

        if (oldValue != null)
        {
            oldValue.IsSelected = false;
            oldValue.Items.CollectionChanged -= SelectedPlaylistItems_CollectionChanged;
        }
        if (newValue != null)
        {
            newValue.IsSelected = true;
            newValue.Items.CollectionChanged += SelectedPlaylistItems_CollectionChanged;

            _ = ResolvePlaylistDurationsAsync(newValue);
        }

        RefreshDisplayedItems();
    }

    private void SelectedPlaylistItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayedItems();
    }

    public void RefreshDisplayedItems()
    {
        if (SelectedPlaylist == null)
        {
            MainThread.BeginInvokeOnMainThread(() => DisplayedItems.Clear());
            return;
        }

        IEnumerable<PlaylistItem> query = SelectedPlaylist.Items;

        // Apply universal sorting
        if (CurrentSortField != SortField.Custom)
        {
            switch (CurrentSortField)
            {
                case SortField.Title:
                    query = IsSortAscending 
                        ? query.OrderBy(x => x.Title) 
                        : query.OrderByDescending(x => x.Title);
                    break;
                case SortField.Duration:
                    query = IsSortAscending 
                        ? query.OrderBy(x => x.Duration) 
                        : query.OrderByDescending(x => x.Duration);
                    break;
                case SortField.LastPlayed:
                    // By defaulting nulls to DateTime.MinValue (infinitely old):
                    // Descending puts Most Recent at the top, and Never Played at the very bottom.
                    // Ascending puts Never Played at the top, and Most Recent at the very bottom.
                    query = IsSortAscending 
                        ? query.OrderBy(x => x.LastPlayedAt ?? DateTime.MinValue) 
                        : query.OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue);
                    break;
            }
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DisplayedItems = new ObservableCollection<PlaylistItem>(query);
        });
    }
}