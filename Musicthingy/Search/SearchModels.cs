using Musicthingy.Models;
using Musicthingy.Settings;

namespace Musicthingy.Search;

/// <summary>
/// Where the search was started. Providers use this to decide how relevant
/// their section is and the coordinator uses it to order the result groups.
/// </summary>
public enum SearchOrigin
{
    PlaylistSidebar,
    OpenPlaylist,
    Settings
}

/// <summary>
/// A result category. New providers (for example a future web search) add a
/// kind here and a provider that returns it; nothing else in the pipeline
/// needs to change.
/// </summary>
public enum SearchSectionKind
{
    Playlists,
    CurrentPlaylistTracks,
    OtherTracks,
    Settings,
    Web
}

/// <summary>A parsed query shared by every provider.</summary>
public sealed class SearchQuery
{
    public static SearchQuery Empty { get; } = new(string.Empty, string.Empty, Array.Empty<string>());

    public string Raw { get; }
    public string Normalized { get; }
    public IReadOnlyList<string> Tokens { get; }
    public bool HasTokens => Tokens.Count > 0;

    private SearchQuery(string raw, string normalized, IReadOnlyList<string> tokens)
    {
        Raw = raw;
        Normalized = normalized;
        Tokens = tokens;
    }

    public static SearchQuery Create(string? raw)
    {
        string trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0) return Empty;

        string normalized = SearchText.Normalize(trimmed);
        if (normalized.Length == 0) return Empty;

        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new SearchQuery(trimmed, normalized, tokens);
    }
}

/// <summary>Fast, allocation-light text normalisation for matching.</summary>
public static class SearchText
{
    /// <summary>Lower-cases and collapses whitespace.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string lower = value.ToLowerInvariant();
        return string.Join(' ', lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>One searchable field and how strongly a hit in it should rank.</summary>
public readonly record struct SearchField(string NormalizedText, int Weight);

/// <summary>
/// Token matching and simple relevance scoring. Every token must appear in at
/// least one field; per-field scores then decide ordering. This is deliberately
/// local and cheap so it can run on a worker thread for a whole library.
/// </summary>
public static class SearchMatcher
{
    public static int Score(SearchQuery query, params SearchField[] fields)
    {
        if (!query.HasTokens || fields.Length == 0) return 0;

        // All tokens must match somewhere (title, tags, playlist name, ...).
        foreach (string token in query.Tokens)
        {
            bool found = false;
            foreach (SearchField field in fields)
            {
                if (field.NormalizedText.Length > 0 &&
                    field.NormalizedText.IndexOf(token, StringComparison.Ordinal) >= 0)
                {
                    found = true;
                    break;
                }
            }

            if (!found) return 0;
        }

        int score = 0;
        foreach (SearchField field in fields)
        {
            if (field.NormalizedText.Length == 0) continue;
            score += ScoreField(field.NormalizedText, query) * field.Weight;
        }

        return Math.Max(score, 1);
    }

    private static int ScoreField(string text, SearchQuery query)
    {
        if (text.Length == 0) return 0;
        if (text == query.Normalized) return 100;

        int score = 0;

        if (text.StartsWith(query.Normalized, StringComparison.Ordinal))
            score += 60;
        else if (ContainsWordStart(text, query.Normalized))
            score += 40;

        foreach (string token in query.Tokens)
        {
            int index = text.IndexOf(token, StringComparison.Ordinal);
            if (index < 0) continue;

            score += 10;
            if (index == 0 || text[index - 1] == ' ') score += 4;
        }

        return score;
    }

    private static bool ContainsWordStart(string text, string phrase)
    {
        int index = text.IndexOf(phrase, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index == 0 || text[index - 1] == ' ') return true;
            index = text.IndexOf(phrase, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}

/// <summary>
/// One row in a result group.
/// <para>
/// <see cref="Icon"/> is the type glyph (playlist, track, setting, ...) and is
/// always available. <see cref="Artwork"/> is the item's own cover/thumbnail
/// when the app already has one cached, so a row can show the real content
/// while the type stays readable from the group header and the corner badge.
/// </para>
/// </summary>
public sealed record SearchResultItem(
    SearchSectionKind Section,
    string Title,
    string Subtitle,
    string Icon,
    object Payload,
    int Score,
    string? Badge = null,
    ImageSource? Artwork = null)
{
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);

    public bool HasArtwork => Artwork is not null;
}

/// <summary>Pre-computed playlist entry for the search index.</summary>
public sealed record PlaylistSearchEntry(
    Playlist Playlist,
    string NormalizedName,
    string NormalizedTags,
    string Subtitle);

/// <summary>Pre-computed track entry for the search index.</summary>
public sealed record TrackSearchEntry(
    PlaylistItem Track,
    Playlist Playlist,
    string NormalizedTitle,
    string NormalizedTags,
    string NormalizedPlaylistName,
    string Subtitle,
    bool IsSystemPlaylist);

/// <summary>Settings are snapshotted per search because descriptions are live.</summary>
public sealed record SettingSearchEntry(
    ISetting Setting,
    string NormalizedTitle,
    string NormalizedDescription,
    string NormalizedCategory,
    string Subtitle,
    string Icon,
    string CategoryName);

/// <summary>Immutable view of the library used by all local providers.</summary>
public sealed record LibrarySearchSnapshot(
    IReadOnlyList<PlaylistSearchEntry> Playlists,
    IReadOnlyList<TrackSearchEntry> Tracks,
    Playlist? SelectedPlaylist,
    int Version)
{
    public static LibrarySearchSnapshot Empty { get; } =
        new(Array.Empty<PlaylistSearchEntry>(), Array.Empty<TrackSearchEntry>(), null, 0);
}

/// <summary>Input for one provider run.</summary>
public sealed record SearchContext(
    SearchOrigin Origin,
    SearchQuery Query,
    LibrarySearchSnapshot Library,
    IReadOnlyList<SettingSearchEntry> Settings);

/// <summary>Output of one provider run.</summary>
public sealed record SearchProviderResult(
    SearchSectionKind Section,
    string Title,
    string Icon,
    int Priority,
    IReadOnlyList<SearchResultItem> Items);

/// <summary>
/// A search category. Register implementations in DI (see
/// SearchServiceCollectionExtensions). Web search is simply another provider.
/// </summary>
public interface ISearchProvider
{
    Task<SearchProviderResult> SearchAsync(SearchContext context, CancellationToken cancellationToken);
}
