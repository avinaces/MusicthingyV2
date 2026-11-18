using Musicthingy.Search;

namespace Musicthingy.Helpers;

/// <summary>
/// Picks the header or item template for the flattened search result rows.
/// </summary>
public sealed class SearchResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        DataTemplate? template = item is SearchResultGroup ? GroupTemplate : ItemTemplate;
        return template ?? throw new InvalidOperationException(
            "SearchResultTemplateSelector needs both GroupTemplate and ItemTemplate.");
    }
}
