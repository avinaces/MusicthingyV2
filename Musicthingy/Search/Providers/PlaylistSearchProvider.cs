using Musicthingy.Helpers;
using Musicthingy.Models;

namespace Musicthingy.Search.Providers;

/// <summary>Matches playlist names and playlist tags.</summary>
public sealed class PlaylistSearchProvider : ISearchProvider
{
    private const int MaxResults = 15;

    public Task<SearchProviderResult> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            List<SearchResultItem> items = new();

            foreach (PlaylistSearchEntry entry in context.Library.Playlists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int score = SearchMatcher.Score(
                    context.Query,
                    new SearchField(entry.NormalizedName, 3),
                    new SearchField(entry.NormalizedTags, 1));

                if (score <= 0) continue;

                items.Add(new SearchResultItem(
                    SearchSectionKind.Playlists,
                    entry.Playlist.Name,
                    entry.Subtitle,
                    "list_music_icon.png",
                    entry.Playlist,
                    score,
                    Artwork: entry.Playlist.CachedIconImageSource));
            }

            SearchResultOrdering.SortAndTrim(items, MaxResults);

            return new SearchProviderResult(
                SearchSectionKind.Playlists,
                "Playlists",
                "list_music_icon.png",
                GetPriority(context.Origin),
                items);
        }, cancellationToken);
    }

    public static int GetPriority(SearchOrigin origin) => origin switch
    {
        SearchOrigin.PlaylistSidebar => 0,
        SearchOrigin.OpenPlaylist => 1,
        _ => 2
    };
}

internal static class SearchResultOrdering
{
    public static void SortAndTrim(List<SearchResultItem> items, int maxResults)
    {
        items.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0
                ? byScore
                : NaturalTitleComparer.Instance.Compare(left.Title, right.Title);
        });

        if (items.Count > maxResults)
            items.RemoveRange(maxResults, items.Count - maxResults);
    }
}
