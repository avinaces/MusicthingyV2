using Musicthingy.Models;
using Musicthingy.Services;
using Musicthingy.Settings;
using System.Diagnostics;

namespace Musicthingy.Search;

/// <summary>
/// Coordinates all registered search providers, applies the origin-specific
/// group order and removes duplicate tracks. Adding a provider (for example web
/// search) requires no changes here.
/// </summary>
public sealed class SearchService
{
    private readonly LibrarySearchIndex _index;
    private readonly SettingsManager _settingsManager;
    private readonly IReadOnlyList<ISearchProvider> _providers;

    public SearchService(
        LibrarySearchIndex index,
        SettingsManager settingsManager,
        IEnumerable<ISearchProvider> providers)
    {
        _index = index;
        _settingsManager = settingsManager;
        _providers = providers.ToList();
    }

    /// <summary>Refresh the cached library snapshot. Must be called on the UI thread.</summary>
    public void UpdateLibrary(IEnumerable<Playlist> playlists, Playlist? selectedPlaylist)
    {
        _index.Update(playlists, selectedPlaylist);
    }

    public async Task<IReadOnlyList<SearchResultGroup>> SearchAsync(
        string rawQuery,
        SearchOrigin origin,
        CancellationToken cancellationToken)
    {
        SearchQuery query = SearchQuery.Create(rawQuery);
        if (!query.HasTokens) return Array.Empty<SearchResultGroup>();

        LibrarySearchSnapshot library = _index.Current;
        IReadOnlyList<SettingSearchEntry> settings = CaptureSettings();
        SearchContext context = new(origin, query, library, settings);

        long started = Stopwatch.GetTimestamp();

        // Local providers run on worker threads; a future web provider can do
        // real async I/O without blocking any of them.
        Task<SearchProviderResult?>[] tasks = _providers
            .Select(provider => SearchSafelyAsync(provider, context, cancellationToken))
            .ToArray();

        SearchProviderResult?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        IReadOnlyList<SearchResultGroup> groups = BuildGroups(results, cancellationToken);

        Debug.WriteLine(
            $"[Search] '{query.Raw}' origin={origin} " +
            $"{(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms, " +
            $"{groups.Count} groups, {groups.Sum(group => group.TotalCount)} hits");

        return groups;
    }

    private static async Task<SearchProviderResult?> SearchSafelyAsync(
        ISearchProvider provider,
        SearchContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Search] Provider {provider.GetType().Name} failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<SearchResultGroup> BuildGroups(
        IEnumerable<SearchProviderResult?> providerResults,
        CancellationToken cancellationToken)
    {
        List<SearchProviderResult> ordered = providerResults
            .Where(result => result is { Items.Count: > 0 })
            .Select(result => result!)
            .OrderBy(result => result.Priority)
            .ToList();

        if (ordered.Count == 0) return Array.Empty<SearchResultGroup>();

        HashSet<string> seenTracks = new(StringComparer.Ordinal);
        List<SearchResultGroup> groups = new(ordered.Count);

        foreach (SearchProviderResult result in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<SearchResultItem> items = new(result.Items.Count);
            foreach (SearchResultItem item in result.Items)
            {
                // The current-playlist group wins the duplicate if a track also
                // appears through another playlist (Cached Tracks, copies, ...).
                if (item.Payload is PlaylistItem track)
                {
                    string key = $"{track.Url}|{track.StartTime}|{track.EndTime}";
                    if (!seenTracks.Add(key)) continue;
                }

                items.Add(item);
            }

            if (items.Count == 0) continue;

            groups.Add(new SearchResultGroup(result.Section, result.Title, result.Icon, items));
        }

        return groups;
    }

    private IReadOnlyList<SettingSearchEntry> CaptureSettings()
    {
        List<SettingSearchEntry> entries = new();

        foreach (ISetting setting in _settingsManager.GetVisibleSettings())
        {
            try
            {
                string description = setting.Description ?? string.Empty;
                string category = setting.Category.ToString();
                string subtitle = string.IsNullOrWhiteSpace(description)
                    ? category
                    : $"{category} · {description}";

                entries.Add(new SettingSearchEntry(
                    setting,
                    SearchText.Normalize(setting.Title),
                    SearchText.Normalize(description),
                    SearchText.Normalize(category),
                    subtitle,
                    string.IsNullOrWhiteSpace(setting.Icon) ? "settings_icon.png" : setting.Icon,
                    category));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Search] Could not describe setting {setting.Key}: {ex.Message}");
            }
        }

        return entries;
    }
}
