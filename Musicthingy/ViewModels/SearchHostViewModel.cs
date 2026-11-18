using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Search;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Musicthingy.ViewModels;

/// <summary>
/// State for one search host (the main header, the sidebar header, the
/// settings header, ...). The origin decides how result groups are ordered.
///
/// The view consumes <see cref="Rows"/>: a flat, virtualisable list of group
/// headers (SearchResultGroup) and result rows (SearchResultItem). Collapsing
/// a group simply rebuilds that list without its items, so headers never
/// disappear and the CollectionView stays a single scrolling surface.
/// </summary>
public sealed partial class SearchHostViewModel : ObservableObject
{
    private const int DebounceMilliseconds = 140;
    private const double PanelHeaderHeight = 46;
    private const double GroupHeaderHeight = 44;
    private const double ResultRowHeight = 58;
    private const double EmptyPanelContentHeight = 148;
    private const double PanelPadding = 12;
    private const double MinPanelHeight = 132;
    private const double MaxAllowedPanelHeight = 560;

    private readonly SearchService _searchService;
    private CancellationTokenSource? _searchCts;
    private double _availableHeight = 640;
    private IReadOnlyList<SearchResultGroup> _groups = Array.Empty<SearchResultGroup>();

    public SearchOrigin Origin { get; }

    public event EventHandler<SearchResultItem>? ResultActivated;
    public event EventHandler? Dismissed;

    [ObservableProperty] public partial string Placeholder { get; set; } = "Search";
    [ObservableProperty] public partial string Query { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial ObservableCollection<object> Rows { get; set; } = new();
    [ObservableProperty] public partial double PanelHeight { get; set; } = MinPanelHeight;
    [ObservableProperty] public partial double MaxPanelHeight { get; set; } = MaxAllowedPanelHeight;

    public bool HasResults => _groups.Count > 0;
    public bool IsEmpty => IsOpen && !IsSearching && _groups.Count == 0;

    public int ResultCount => _groups.Sum(group => group.TotalCount);

    public string ResultsSummary
    {
        get
        {
            if (IsSearching && _groups.Count == 0) return "Searching…";
            if (_groups.Count == 0) return "Search results";

            int count = ResultCount;
            return count == 1 ? "1 result" : $"{count} results";
        }
    }

    public string EmptyMessage
    {
        get
        {
            string query = (Query ?? string.Empty).Trim();
            return query.Length == 0
                ? "No matches."
                : $"Nothing matches “{query}”.";
        }
    }

    public SearchHostViewModel(SearchService searchService, SearchOrigin origin)
    {
        _searchService = searchService;
        Origin = origin;
    }

    public void SetAvailableHeight(double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) return;

        _availableHeight = height;
        RecalculatePanel();
    }

    /// <summary>Shows existing results again when the user re-focuses the entry.</summary>
    public void NotifyFocused()
    {
        if ((Query ?? string.Empty).Trim().Length > 0) IsOpen = true;
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyMessage));
        _ = RunSearchAsync(value);
    }

    partial void OnIsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ResultsSummary));
    }

    private async Task RunSearchAsync(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        CancellationToken token = _searchCts.Token;

        string query = (value ?? string.Empty).Trim();

        if (query.Length == 0)
        {
            IsSearching = false;
            SetGroups(Array.Empty<SearchResultGroup>());
            IsOpen = false;
            return;
        }

        IsOpen = true;
        IsSearching = true;

        try
        {
            await Task.Delay(DebounceMilliseconds, token);

            IReadOnlyList<SearchResultGroup> groups =
                await _searchService.SearchAsync(query, Origin, token);

            token.ThrowIfCancellationRequested();
            SetGroups(groups);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this search.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Search] Host search failed: {ex.Message}");
            if (!token.IsCancellationRequested)
                SetGroups(Array.Empty<SearchResultGroup>());
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
                RecalculatePanel();
            }
        }
    }

    private void SetGroups(IReadOnlyList<SearchResultGroup> groups)
    {
        _groups = groups;
        RebuildRows();
    }

    private void RebuildRows()
    {
        ObservableCollection<object> rows = new();

        foreach (SearchResultGroup group in _groups)
        {
            rows.Add(group);

            if (!group.IsExpanded) continue;

            foreach (SearchResultItem item in group.AllItems)
                rows.Add(item);
        }

        Rows = rows;

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(ResultCount));
        RecalculatePanel();
    }

    private void RecalculatePanel()
    {
        double maxHeight = Math.Max(MinPanelHeight, Math.Min(MaxAllowedPanelHeight, _availableHeight - 24));
        MaxPanelHeight = maxHeight;

        double content = PanelPadding + PanelHeaderHeight;
        if (_groups.Count == 0)
        {
            content += EmptyPanelContentHeight;
        }
        else
        {
            foreach (SearchResultGroup group in _groups)
            {
                content += GroupHeaderHeight;
                if (group.IsExpanded) content += group.TotalCount * ResultRowHeight;
            }
        }

        PanelHeight = Math.Clamp(content, MinPanelHeight, maxHeight);
    }

    [RelayCommand]
    private void ToggleGroup(SearchResultGroup? group)
    {
        if (group == null) return;

        group.IsExpanded = !group.IsExpanded;
        RebuildRows();
    }

    [RelayCommand]
    private void ActivateResult(SearchResultItem? item)
    {
        if (item == null) return;

        Dismiss();
        ResultActivated?.Invoke(this, item);
    }

    [RelayCommand]
    private void Close() => Dismiss();

    private void Dismiss()
    {
        bool wasOpen = IsOpen;

        // Clearing the query also clears rows and restores the placeholder.
        Query = string.Empty;
        IsOpen = false;
        IsSearching = false;

        if (wasOpen)
            Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
