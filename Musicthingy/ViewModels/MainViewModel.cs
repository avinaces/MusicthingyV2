using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Graphics;
using Musicthingy.Helpers;
using Musicthingy.Models;
using Musicthingy.Search;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Musicthingy.ViewModels;

public enum SortField
{
    Custom,
    Title,
    Duration,
    LastPlayed,
    Views
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static Action? _cleanupOldInstances;

    private CancellationTokenSource? _playlistResolutionCts;

    private readonly PlaylistDataService _dataService;
    private readonly ColorExtractionService _colorExtractionService;
    private readonly SettingsManager _settingsManager;
    private readonly TrackCacheService _trackCacheService;
    private readonly SearchService _searchService;

    [ObservableProperty] public partial bool IsLoadingMedia { get; set; }
    [ObservableProperty] public partial double MediaDownloadProgress { get; set; }
    [ObservableProperty] public partial ObservableCollection<Playlist> Playlists { get; set; } = new();
    [ObservableProperty] public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty] public partial ObservableCollection<PlaylistItem> DisplayedItems { get; set; } = new();

    // ==========================================
    // MULTI-SELECTION MODE
    // ==========================================
    [ObservableProperty] public partial bool IsTrackSelectionMode { get; set; }
    [ObservableProperty] public partial bool IsPlaylistSelectionMode { get; set; }
    [ObservableProperty] public partial int SelectedTrackCount { get; set; }
    [ObservableProperty] public partial int SelectedPlaylistCount { get; set; }

    public bool IsSelectionToolbarVisible => SelectedTrackCount > 0 || SelectedPlaylistCount > 0;

    public string SelectionCountText
    {
        get
        {
            var parts = new List<string>();
            if (SelectedPlaylistCount > 0) parts.Add($"{SelectedPlaylistCount} playlist{(SelectedPlaylistCount > 1 ? "s" : "")}");
            if (SelectedTrackCount > 0) parts.Add($"{SelectedTrackCount} track{(SelectedTrackCount > 1 ? "s" : "")}");
            return parts.Count > 0 ? string.Join(", ", parts) + " selected" : "";
        }
    }

    private int _playlistSelectionCounter = 0;

    partial void OnIsTrackSelectionModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var item in DisplayedItems) item.IsMultiSelected = false;
            SelectedTrackCount = 0;
            OnPropertyChanged(nameof(IsSelectionToolbarVisible));
            OnPropertyChanged(nameof(SelectionCountText));
        }
    }

    partial void OnIsPlaylistSelectionModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var p in Playlists) { p.IsMultiSelected = false; p.SelectionOrder = 0; }
            SelectedPlaylistCount = 0;
            _playlistSelectionCounter = 0;
            OnPropertyChanged(nameof(IsSelectionToolbarVisible));
            OnPropertyChanged(nameof(SelectionCountText));
        }
    }

    public void ToggleItemSelection(PlaylistItem item)
    {
        item.IsMultiSelected = !item.IsMultiSelected;
        if (item.IsMultiSelected && !IsTrackSelectionMode) IsTrackSelectionMode = true;
        UpdateSelectedTrackCount();
    }

    public void UpdateSelectedCountPublic() => UpdateSelectedTrackCount();
    private void UpdateSelectedTrackCount()
    {
        SelectedTrackCount = DisplayedItems.Count(i => i.IsMultiSelected);
        if (SelectedTrackCount == 0 && IsTrackSelectionMode) IsTrackSelectionMode = false;
        OnPropertyChanged(nameof(IsSelectionToolbarVisible));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    public void TogglePlaylistSelection(Playlist playlist)
    {
        playlist.IsMultiSelected = !playlist.IsMultiSelected;
        if (playlist.IsMultiSelected)
        {
            _playlistSelectionCounter++;
            playlist.SelectionOrder = _playlistSelectionCounter;
            if (!IsPlaylistSelectionMode) IsPlaylistSelectionMode = true;
        }
        else playlist.SelectionOrder = 0;
        UpdateSelectedPlaylistCount();
    }

    private void UpdateSelectedPlaylistCount()
    {
        SelectedPlaylistCount = Playlists.Count(p => p.IsMultiSelected);
        if (SelectedPlaylistCount == 0 && IsPlaylistSelectionMode) IsPlaylistSelectionMode = false;
        OnPropertyChanged(nameof(IsSelectionToolbarVisible));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    private void ToggleSelectAllTracks()
    {
        bool allSelected = DisplayedItems.All(i => i.IsMultiSelected);
        foreach (var item in DisplayedItems) item.IsMultiSelected = !allSelected;
        if (!allSelected && !IsTrackSelectionMode) IsTrackSelectionMode = true;
        UpdateSelectedTrackCount();
    }

    [RelayCommand]
    private void ToggleSelectAllPlaylists()
    {
        bool allSelected = Playlists.All(p => p.IsMultiSelected);
        if (allSelected)
        {
            foreach (var p in Playlists) { p.IsMultiSelected = false; p.SelectionOrder = 0; }
            _playlistSelectionCounter = 0;
        }
        else
        {
            foreach (var p in Playlists)
            {
                if (!p.IsMultiSelected)
                {
                    p.IsMultiSelected = true;
                    _playlistSelectionCounter++;
                    p.SelectionOrder = _playlistSelectionCounter;
                }
            }
            if (!IsPlaylistSelectionMode) IsPlaylistSelectionMode = true;
        }
        UpdateSelectedPlaylistCount();
    }

    private List<PlaylistItem> CollectSelectedItems()
    {
        var result = new List<PlaylistItem>();
        var seen = new HashSet<string>();
        foreach (var playlist in Playlists.Where(p => p.IsMultiSelected).OrderBy(p => p.SelectionOrder))
            foreach (var item in playlist.Items)
                if (seen.Add(item.Url)) result.Add(item);
        foreach (var item in DisplayedItems.Where(i => i.IsMultiSelected))
            if (seen.Add(item.Url)) result.Add(item);
        return result;
    }

    [RelayCommand]
    private void PlaySelected()
    {
        var items = CollectSelectedItems();
        if (items.Count == 0) return;
        IsShuffleEnabled = false;
        SetupPlaybackQueue(items);
        IsTrackSelectionMode = false;
        IsPlaylistSelectionMode = false;
    }

    [RelayCommand]
    private void ShuffleSelected()
    {
        var items = CollectSelectedItems();
        if (items.Count == 0) return;
        IsShuffleEnabled = true;
        SetupPlaybackQueue(items);
        IsTrackSelectionMode = false;
        IsPlaylistSelectionMode = false;
    }

    [RelayCommand]
    private void CancelSelection()
    {
        IsTrackSelectionMode = false;
        IsPlaylistSelectionMode = false;
    }

    // ==========================================
    // TAG FILTERING LOGIC
    // ==========================================

    public ObservableCollection<FilterTag> CurrentFilterTags { get; set; } = new();

    public bool IsFilterActive => CurrentFilterTags.Any(t => t.State != FilterState.Neutral);

    public Color FilterIconColor => IsFilterActive ? ThemeResources.Color("AccentColor") : ThemeResources.Color("TextColor");

    public string FilterToolbarIcon => IsFilterActive ? "funnel_x_icon.png" : "funnel_icon.png";

    [RelayCommand]
    private async Task OpenFilterSheet()
    {
        var sheet = new Views.FilterOptionsSheet(this);
        if (Services.SheetManager.Host != null)
        {
            await Services.SheetManager.Host.ShowAsync(sheet);
        }
    }

    [RelayCommand]
    private async Task ToolbarFilterIconActionAsync()
    {
        if (IsFilterActive)
        {
            ClearFilter();
        }
        else
        {
            await OpenFilterSheet();
        }
    }

    [RelayCommand]
    private void ToggleFilterTag(FilterTag tag)
    {
        tag.State = tag.State switch
        {
            FilterState.Neutral => FilterState.Included,
            FilterState.Included => FilterState.Excluded,
            FilterState.Excluded => FilterState.Neutral,
            _ => FilterState.Neutral
        };

        NotifyFilterStateChanged();
        RefreshDisplayedItems();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        foreach (FilterTag tag in CurrentFilterTags)
        {
            tag.State = FilterState.Neutral;
        }

        NotifyFilterStateChanged();
        RefreshDisplayedItems();
    }

    private void NotifyFilterStateChanged()
    {
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterIconColor));
        OnPropertyChanged(nameof(FilterToolbarIcon));
    }


    // ==========================================
    // UNIVERSAL SORTING LOGIC & UI HELPERS
    // ==========================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSortModeText))]
    [NotifyPropertyChangedFor(nameof(IsSortCustom))]
    [NotifyPropertyChangedFor(nameof(IsSortTitle))]
    [NotifyPropertyChangedFor(nameof(IsSortDuration))]
    [NotifyPropertyChangedFor(nameof(IsSortLastPlayed))]
    [NotifyPropertyChangedFor(nameof(IsSortViews))]
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
        SortField.Views => "Views",
        _ => "Custom Order"
    };

    public bool IsSortCustom => CurrentSortField == SortField.Custom;
    public bool IsSortTitle => CurrentSortField == SortField.Title;
    public bool IsSortDuration => CurrentSortField == SortField.Duration;
    public bool IsSortLastPlayed => CurrentSortField == SortField.LastPlayed;
    public bool IsSortViews => CurrentSortField == SortField.Views;

    public bool IsDirectionToggleVisible => CurrentSortField != SortField.Custom;
    public string SortDirectionIcon => IsSortAscending ? "arrow_down_icon.png" : "arrow_up_icon.png";

    [RelayCommand]
    private void SetSortField(SortField field)
    {
        if (CurrentSortField != field)
        {
            CurrentSortField = field;
            if (field == SortField.LastPlayed)
                IsSortAscending = false;
            else
                IsSortAscending = true;
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
    // CORE INITIALIZATION & CLEANUP
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
        IMediaProbingService mediaProbingService,
        ColorExtractionService colorExtractionService,
        SettingsManager settingsManager,
        TrackCacheService trackCacheService,
        SearchService searchService)
    {
        _themeManager = themeManager;
        _youtubeResolver = youtubeResolver;
        _playbackService = playbackService;
        _mediaProbingService = mediaProbingService;
        _colorExtractionService = colorExtractionService;
        _settingsManager = settingsManager;
        _trackCacheService = trackCacheService;
        _searchService = searchService;


        _cleanupOldInstances?.Invoke();
        _cleanupOldInstances = CleanupMedia;

        _dataService = new PlaylistDataService();

        InitializePlaybackService();

        Task.Run(LoadStateAsync);

        WeakReferenceMessenger.Default.Register<MediaControlMessage>(this, (r, m) => OnMediaControlMessage(m));

#if ANDROID
        // A VIEW / PLAY_VIDEO intent delivered before this registration was
        // buffered by MainActivity - drain it now so a cold start from another
        // app still plays. (Runs on the UI thread; the handler starts async work.)
        if (global::Musicthingy.MainActivity.ConsumePendingLaunchMessage() is { } pending)
            OnMediaControlMessage(pending);
#endif
    }

    /// <summary>
    /// Single entry point for OS/notification/other-app media commands.
    /// Sets <see cref="MediaControlMessage.Consumed"/> so MainActivity knows the
    /// message was handled and does not need to buffer it for a later start.
    /// </summary>
    private void OnMediaControlMessage(MediaControlMessage message)
    {
        message.Consumed = true;

        if (message.Action == "Next" && PlayNextCommand.CanExecute(null))
            PlayNextCommand.Execute(null);
        else if (message.Action == "Previous" && PlayPreviousCommand.CanExecute(null))
            PlayPreviousCommand.Execute(null);
        else if (message.Action == "PlayPause" && TogglePlayCommand.CanExecute(null))
            TogglePlayCommand.Execute(null);
        else if (message.Action == "ScrollToCurrentTrack")
            ScrollToCurrentTrack();
        else if (message.Action == "PlayVideo" && !string.IsNullOrEmpty(message.VideoId))
            _ = PlayVideoByIdAsync(message.VideoId, message.StartSeconds);
        else if (message.Action == "PlayUri" && !string.IsNullOrEmpty(message.Uri))
            _ = PlayExternalUriAsync(message);
    }

    // Partial declaration bridging to the platform-specific files
    partial void CleanupOSMediaPlatform();

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

        // Call the platform specific method (no compiler directives needed)
        CleanupOSMediaPlatform();
    }

    public void Dispose()
    {
        try { CleanupMedia(); } catch { }
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedPlaylistChanged(Playlist? oldValue, Playlist? newValue)
    {
        RefreshSearchIndex();

        if (!SettingsManager.IsRestoringState)
        {
            Preferences.Default.Set("SelectedPlaylistName", newValue?.Name ?? string.Empty);
        }

        if (oldValue != null)
        {
            oldValue.IsSelected = false;
            oldValue.Items.CollectionChanged -= SelectedPlaylistItems_CollectionChanged;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentFilterTags = new ObservableCollection<FilterTag>();
            NotifyFilterStateChanged();

            if (newValue != null)
            {
                newValue.IsSelected = true;
                newValue.Items.CollectionChanged += SelectedPlaylistItems_CollectionChanged;

                _ = ResolvePlaylistDurationsAsync(newValue);
            }

            UpdateFilterTags();

            // Cached Tracks is generated from the cache; if it went stale
            // while another playlist was open, rebuild it on entry.
            if (newValue == _cachedTracksPlaylist &&
                Volatile.Read(ref _cachedTracksBuiltVersion) != Volatile.Read(ref _cachedTracksVersion))
            {
                RefreshCachedTracksPlaylist();
            }

            RefreshDisplayedItems(forceReset: true);
        });
    }

    private void SelectedPlaylistItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateFilterTags();
        RefreshDisplayedItems();
    }

    public void UpdateFilterTags()
    {
        Playlist? currentPlaylist = SelectedPlaylist;
        if (currentPlaylist == null) return;

        var itemsSnapshot = currentPlaylist.Items.ToList();
        var playlistTagsSnapshot = currentPlaylist.Tags?.ToList() ?? new List<string>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            HashSet<string> allTagsInPlaylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string t in playlistTagsSnapshot) allTagsInPlaylist.Add(t);

            foreach (PlaylistItem item in itemsSnapshot)
            {
                if (item.Tags != null)
                {
                    foreach (string t in item.Tags) allTagsInPlaylist.Add(t);
                }
            }

            List<FilterTag> newTags = CurrentFilterTags.ToList();
            bool changed = false;

            foreach (string tag in allTagsInPlaylist)
            {
                if (!newTags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    newTags.Add(new FilterTag { Name = tag, State = FilterState.Neutral });
                    changed = true;
                }
            }

            for (int i = newTags.Count - 1; i >= 0; i--)
            {
                if (!allTagsInPlaylist.Contains(newTags[i].Name))
                {
                    newTags.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed)
            {
                CurrentFilterTags = new ObservableCollection<FilterTag>(newTags.OrderBy(t => t.Name));
                NotifyFilterStateChanged();
            }
        });
    }

    public void RefreshDisplayedItems(bool forceReset = false)
    {
        Playlist? currentPlaylist = SelectedPlaylist;

        if (currentPlaylist == null)
        {
            MainThread.BeginInvokeOnMainThread(() => DisplayedItems.Clear());
            return;
        }

        List<PlaylistItem> sourceItems = currentPlaylist.Items.ToList();
        List<string> playlistTagsSnapshot = currentPlaylist.Tags?.ToList() ?? new List<string>();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            IEnumerable<PlaylistItem> query = sourceItems;

            List<string> includedTags = CurrentFilterTags.Where(t => t.State == FilterState.Included).Select(t => t.Name).ToList();
            List<string> excludedTags = CurrentFilterTags.Where(t => t.State == FilterState.Excluded).Select(t => t.Name).ToList();

            if (includedTags.Count > 0 || excludedTags.Count > 0)
            {
                query = query.Where(item =>
                {
                    HashSet<string> itemTags = new HashSet<string>(playlistTagsSnapshot, StringComparer.OrdinalIgnoreCase);
                    if (item.Tags != null)
                    {
                        foreach (string t in item.Tags) itemTags.Add(t);
                    }

                    bool hasAllIncluded = includedTags.All(inc => itemTags.Contains(inc));
                    bool hasAnyExcluded = excludedTags.Any(exc => itemTags.Contains(exc));

                    return hasAllIncluded && !hasAnyExcluded;
                });
            }

            if (CurrentSortField != SortField.Custom)
            {
                switch (CurrentSortField)
                {
                    case SortField.Title:
                        query = IsSortAscending
                            ? query.OrderBy(x => x.Title, NaturalTitleComparer.Instance)
                            : query.OrderByDescending(x => x.Title, NaturalTitleComparer.Instance);
                        break;
                    case SortField.Duration:
                        query = IsSortAscending
                            ? query.OrderBy(x => x.Duration)
                            : query.OrderByDescending(x => x.Duration);
                        break;
                    case SortField.LastPlayed:
                        query = IsSortAscending
                            ? query.OrderBy(x => x.LastPlayedAt ?? DateTime.MinValue)
                            : query.OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue);
                        break;
                    case SortField.Views:
                        query = IsSortAscending
                            ? query.OrderBy(x => x.Views)
                            : query.OrderByDescending(x => x.Views);
                        break;
                }
            }

            List<PlaylistItem> newItems = query.ToList();

            if (forceReset || DisplayedItems.Count != newItems.Count)
            {
                DisplayedItems = new ObservableCollection<PlaylistItem>(newItems);
            }
            else
            {
                for (int i = 0; i < newItems.Count; i++)
                {
                    if (DisplayedItems[i] != newItems[i])
                    {
                        DisplayedItems[i] = newItems[i];
                    }
                }
            }
        });
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        var settingsPage = new Views.SettingsPage(_settingsManager, _searchService);

        if (Application.Current is { } app && app.Windows.Count > 0)
        {
            var mainPage = app.Windows[0]?.Page;
            if (mainPage != null)
            {
                await mainPage.Navigation.PushModalAsync(settingsPage);
            }
        }
    }
}