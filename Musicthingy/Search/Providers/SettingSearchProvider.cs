namespace Musicthingy.Search.Providers;

/// <summary>Matches settings by title, description and category.</summary>
public sealed class SettingSearchProvider : ISearchProvider
{
    private const int MaxResults = 12;

    public Task<SearchProviderResult> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            List<SearchResultItem> items = new();

            foreach (SettingSearchEntry entry in context.Settings)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int score = SearchMatcher.Score(
                    context.Query,
                    new SearchField(entry.NormalizedTitle, 3),
                    new SearchField(entry.NormalizedDescription, 1),
                    new SearchField(entry.NormalizedCategory, 1));

                if (score <= 0) continue;

                items.Add(new SearchResultItem(
                    SearchSectionKind.Settings,
                    entry.Setting.Title,
                    entry.Subtitle,
                    entry.Icon,
                    entry.Setting,
                    score,
                    entry.CategoryName));
            }

            SearchResultOrdering.SortAndTrim(items, MaxResults);

            return new SearchProviderResult(
                SearchSectionKind.Settings,
                "Settings",
                "settings_icon.png",
                GetPriority(context.Origin),
                items);
        }, cancellationToken);
    }

    public static int GetPriority(SearchOrigin origin) => origin switch
    {
        SearchOrigin.Settings => 0,
        _ => 3
    };
}
