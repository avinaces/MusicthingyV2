using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Maui.Graphics;
using Musicthingy.Helpers;
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

    [ObservableProperty] public partial bool IsLoadingMedia { get; set; }
    [ObservableProperty] public partial double MediaDownloadProgress { get; set; }
    [ObservableProperty] public partial ObservableCollection<Playlist> Playlists { get; set; } = new();
    [ObservableProperty] public partial Playlist? SelectedPlaylist { get; set; }

    [ObservableProperty] public partial ObservableCollection<PlaylistItem> DisplayedItems { get; set; } = new();

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
        SettingsManager settingsManager)
    {
        _themeManager = themeManager;
        _youtubeResolver = youtubeResolver;
        _playbackService = playbackService;
        _mediaProbingService = mediaProbingService;
        _colorExtractionService = colorExtractionService;
        _settingsManager = settingsManager;


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
            else if (m.Action == "AutoFollow")         
                TriggerAutoFollow();                   
        });
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
        var settingsPage = new Views.SettingsPage(_settingsManager);

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