using Musicthingy.Search;
using Musicthingy.Settings;

namespace Musicthingy.ViewModels;

public partial class MainViewModel
{
    /// <summary>Refreshes the snapshot the search providers read on keystrokes.</summary>
    private void RefreshSearchIndex()
    {
        _searchService.UpdateLibrary(Playlists, SelectedPlaylist);
    }

    /// <summary>
    /// Opens the settings category that contains a search result and scrolls
    /// that row into view. Used by the search menu in the main header/sidebar.
    /// </summary>
    public async Task OpenSettingAsync(ISetting setting)
    {
        Page? host = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (host is null) return;

        Page top = host.Navigation.ModalStack.Count > 0
            ? host.Navigation.ModalStack[^1]
            : host;

        await top.Navigation.PushModalAsync(
            new Views.SettingsCategoryPage(_settingsManager, setting.Category, setting));
    }
}
