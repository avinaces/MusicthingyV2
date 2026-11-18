using Musicthingy.Models;

namespace Musicthingy.Search.Providers;

/// <summary>
/// Matches tracks across the whole library. Tracks that belong to the open
/// playlist are handled by <see cref="CurrentPlaylistTrackSearchProvider"/> so
/// they can rank as their own category.
/// </summary>
public sealed class LibraryTrackSearchProvider : ISearchProvider
{
    private const int MaxResults = 20;

    public Task<SearchProviderResult> SearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            List<SearchResultItem> items = new();
            Playlist? selected = context.Library.SelectedPlaylist;

            foreach (TrackSearchEntry entry in context.Library.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The open playlist has its own group.
                if (selected != null && ReferenceEquals(entry.Playlist, selected)) continue;

                // Cached Tracks is a generated view of other playlists; searching
                // it would list the same files twice.
                if (entry.IsSystemPlaylist) continue;

                int score = SearchMatcher.Score(
                    context.Query,
                    new SearchField(entry.NormalizedTitle, 3),
                    new SearchField(entry.NormalizedTags, 1),
                    new SearchField(entry.NormalizedPlaylistName, 1));

                if (score <= 0) continue;

                items.Add(new SearchResultItem(
                    SearchSectionKind.OtherTracks,
                    entry.Track.Title,
                    entry.Subtitle,
                    "music_4_icon.png",
                    entry.Track,
                    score,
                    Artwork: entry.Track.CachedItemIconSource));
            }

            SearchResultOrdering.SortAndTrim(items, MaxResults);

            return new SearchProviderResult(
                SearchSectionKind.OtherTracks,
                selected == null ? "Tracks" : "Other tracks",
                "music_4_icon.png",
                GetPriority(context.Origin),
                items);
        }, cancellationToken);
    }

    public static int GetPriority(SearchOrigin origin) => origin switch
    {
        SearchOrigin.OpenPlaylist => 1,
        SearchOrigin.PlaylistSidebar => 2,
        _ => 3
    };
}
