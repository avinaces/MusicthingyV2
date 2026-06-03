using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Musicthingy.Models;
using Musicthingy.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Musicthingy.ViewModels;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using System;

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
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    [NotifyPropertyChangedFor(nameof(FilterIconColor))]
    public partial ObservableCollection<FilterTag> CurrentFilterTags { get; set; } = new();

    public bool IsFilterActive => CurrentFilterTags.Any(t => t.State != FilterState.Neutral);
    public Microsoft.Maui.Graphics.Color FilterIconColor => IsFilterActive ? ThemeResources.Color("AccentColor") : ThemeResources.Color("MutedColor");

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
    private void ToggleFilterTag(FilterTag tag)
    {
        tag.State = tag.State switch
        {
            FilterState.Neutral => FilterState.Included,
            FilterState.Included => FilterState.Excluded,
            FilterState.Excluded => FilterState.Neutral,
            _ => FilterState.Neutral
        };

        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterIconColor));
        RefreshDisplayedItems();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        foreach (FilterTag tag in CurrentFilterTags)
        {
            tag.State = FilterState.Neutral;
        }
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(FilterIconColor));
        RefreshDisplayedItems();
    }

    public void UpdateFilterTags()
    {
        if (SelectedPlaylist == null) return;

        HashSet<string> allTagsInPlaylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SelectedPlaylist.Tags != null)
        {
            foreach (string t in SelectedPlaylist.Tags) allTagsInPlaylist.Add(t);
        }
        foreach (PlaylistItem item in SelectedPlaylist.Items)
        {
            if (item.Tags != null)
            {
                foreach (string t in item.Tags) allTagsInPlaylist.Add(t);
            }
        }

        HashSet<string> currentTagNames = new HashSet<string>(CurrentFilterTags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        bool changed = false;
        foreach (string tag in allTagsInPlaylist)
        {
            if (!currentTagNames.Contains(tag))
            {
                CurrentFilterTags.Add(new FilterTag { Name = tag, State = FilterState.Neutral });
                changed = true;
            }
        }

        for (int i = CurrentFilterTags.Count - 1; i >= 0; i--)
        {
            if (!allTagsInPlaylist.Contains(CurrentFilterTags[i].Name))
            {
                CurrentFilterTags.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            List<FilterTag> sorted = CurrentFilterTags.OrderBy(t => t.Name).ToList();
            CurrentFilterTags.Clear();
            foreach (FilterTag t in sorted) CurrentFilterTags.Add(t);

            OnPropertyChanged(nameof(IsFilterActive));
            OnPropertyChanged(nameof(FilterIconColor));
        }
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
        UpdateFilterTags();
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
        // Apply tag filtering
        List<string> includedTags = CurrentFilterTags.Where(t => t.State == FilterState.Included).Select(t => t.Name).ToList();
        List<string> excludedTags = CurrentFilterTags.Where(t => t.State == FilterState.Excluded).Select(t => t.Name).ToList();

        if (includedTags.Count > 0 || excludedTags.Count > 0)
        {
            query = query.Where(item => 
            {
                List<string> itemTags = (SelectedPlaylist.Tags ?? Enumerable.Empty<string>())
                    .Concat(item.Tags ?? new ObservableCollection<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool hasAllIncluded = includedTags.All(inc => itemTags.Contains(inc, StringComparer.OrdinalIgnoreCase));
                bool hasAnyExcluded = excludedTags.Any(exc => itemTags.Contains(exc, StringComparer.OrdinalIgnoreCase));

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

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // If the item count has changed (e.g., switching playlists or adding/removing a track),
            // a full reset is necessary.
            if (DisplayedItems.Count != newItems.Count)
            {
                DisplayedItems = new ObservableCollection<PlaylistItem>(newItems);
            }
            else
            {
                // By replacing the items in-place, the collection never fires a full "Reset",
                // meaning the VirtualizeListView never collapses to 0 height. 
                // This keeps your current scroll viewport perfectly still!
                for (int i = 0; i < newItems.Count; i++)
                {
                    // Only trigger UI updates for items that actually swapped places
                    if (DisplayedItems[i] != newItems[i])
                    {
                        DisplayedItems[i] = newItems[i];
                    }
                }
            }
        });
    }
}