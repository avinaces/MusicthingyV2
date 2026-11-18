using Musicthingy.Search.Providers;

namespace Musicthingy.Search;

public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the search pipeline. To add a new category (for example web
    /// results), implement <see cref="ISearchProvider"/> and register it here.
    /// </summary>
    public static IServiceCollection AddSearch(this IServiceCollection services)
    {
        services.AddSingleton<LibrarySearchIndex>();
        services.AddSingleton<ISearchProvider, PlaylistSearchProvider>();
        services.AddSingleton<ISearchProvider, CurrentPlaylistTrackSearchProvider>();
        services.AddSingleton<ISearchProvider, LibraryTrackSearchProvider>();
        services.AddSingleton<ISearchProvider, SettingSearchProvider>();
        services.AddSingleton<SearchService>();

        return services;
    }
}
