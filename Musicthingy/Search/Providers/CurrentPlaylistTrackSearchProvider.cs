using Musicthingy.Models;

namespace Musicthingy.Search.Providers;

/// <summary>Matches tracks inside the playlist that is currently open.</summary>
public sealed class CurrentPlaylistTrackSearchProvider : ISearchProvider
{
    private const int MaxResults = 15;

    public Task<SearchProviderResult> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            List<SearchResultItem> items = new();
            Playlist? selected = context.Library.SelectedPlaylist;
            string title = selected == null ? "Tracks in playlist" : $"In {selected.Name}";

            if (selected != null)
            {
                foreach (TrackSearchEntry entry in context.Library.Tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ReferenceEquals(entry.Playlist, selected)) continue;

                    int score = SearchMatcher.Score(
                        context.Query,
                        new SearchField(entry.NormalizedTitle, 3),
                        new SearchField(entry.NormalizedTags, 1));

                    if (score <= 0) continue;

                    items.Add(new SearchResultItem(
                        SearchSectionKind.CurrentPlaylistTracks,
                        entry.Track.Title,
                        entry.Subtitle,
                        "music_4_icon.png",
                        entry.Track,
                        score,
                        Artwork: entry.Track.CachedItemIconSource));
                }
            }

            SearchResultOrdering.SortAndTrim(items, MaxResults);

            return new SearchProviderResult(
                SearchSectionKind.CurrentPlaylistTracks,
                title,
                "music_4_icon.png",
                GetPriority(context.Origin),
                items);
        }, cancellationToken);
    }

    public static int GetPriority(SearchOrigin origin) => origin switch
    {
        SearchOrigin.OpenPlaylist => 0,
        SearchOrigin.PlaylistSidebar => 1,
        _ => 2
    };
}
