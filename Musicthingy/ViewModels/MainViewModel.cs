using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Maui.Graphics;

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
    // TAG FILTERING LOGIC
    // ==========================================

    [ObservableProperty]
    public partial ObservableCollection<FilterTag> CurrentFilterTags { get; set; } = new();

    public bool IsFilterActive => CurrentFilterTags.Any(t => t.State != FilterState.Neutral);

    // CHANGED: Fallback color changed from MutedColor to TextColor so the text perfectly matches the icon's tint
    public Color FilterIconColor => IsFilterActive ? ThemeResources.Color("AccentColor") : ThemeResources.Color("TextColor");

    // Adjusted to standard Lucide logic as requested
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
        // Acts as 'X' to clear if active, or 'Funnel' to open sheet if inactive.
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

        // CRITICAL MAUI CRASH FIX: Assign a brand new collection instead of calling .Clear()
        // This prevents disposed BottomSheets from receiving collection changed events
        // and attempting to manipulate destroyed Android ViewGroups.
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

            // CRITICAL MAUI CRASH FIX: Do all logic on a temporary standard List
            // Never call .Add() or .RemoveAt() on the bound CurrentFilterTags directly
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
                // Assign a brand new ObservableCollection to safely trigger the UI update
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

        // Snapshot current items to avoid collection modified exceptions on Playlist.Items
        var sourceItems = currentPlaylist.Items.ToList(); 
        var playlistTagsSnapshot = currentPlaylist.Tags?.ToList() ?? new List<string>();

        // Push ALL filtering logic to the MainThread. This guarantees CurrentFilterTags 
        // won't be modified by a playlist switch at the exact moment we are iterating it.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IEnumerable<PlaylistItem> query = sourceItems;

            // Apply tag filtering safely
            List<string> includedTags = CurrentFilterTags.Where(t => t.State == FilterState.Included).Select(t => t.Name).ToList();
            List<string> excludedTags = CurrentFilterTags.Where(t => t.State == FilterState.Excluded).Select(t => t.Name).ToList();

            if (includedTags.Count > 0 || excludedTags.Count > 0)
            {
                query = query.Where(item => 
                {
                    var itemTags = new HashSet<string>(playlistTagsSnapshot, StringComparer.OrdinalIgnoreCase);
                    if (item.Tags != null)
                    {
                        foreach (string t in item.Tags) itemTags.Add(t);
                    }

                    bool hasAllIncluded = includedTags.All(inc => itemTags.Contains(inc));
                    bool hasAnyExcluded = excludedTags.Any(exc => itemTags.Contains(exc));

                    return hasAllIncluded && !hasAnyExcluded;
                });
            }

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
                        query = IsSortAscending
                            ? query.OrderBy(x => x.LastPlayedAt ?? DateTime.MinValue)
                            : query.OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue);
                        break;
                }
            }

            var newItems = query.ToList();

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
}