using CommunityToolkit.Mvvm.ComponentModel;

namespace Musicthingy.Search;

/// <summary>
/// One expandable category in the search menu. A group is rendered as a
/// CollectionView row by the host view model; collapsing simply omits its
/// item rows, so the header always stays visible and tappable.
/// </summary>
public sealed partial class SearchResultGroup : ObservableObject
{
    public SearchSectionKind Section { get; }
    public string Title { get; }
    public string Icon { get; }
    public IReadOnlyList<SearchResultItem> AllItems { get; }
    public int TotalCount => AllItems.Count;
    public string CountText => TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;

    public SearchResultGroup(
        SearchSectionKind section,
        string title,
        string icon,
        IReadOnlyList<SearchResultItem> items)
    {
        Section = section;
        Title = title;
        Icon = icon;
        AllItems = items;
    }
}
