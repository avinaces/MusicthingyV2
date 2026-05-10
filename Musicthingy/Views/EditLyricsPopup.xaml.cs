using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;

namespace Musicthingy.Views;

public partial class EditLyricsPopup : Popup<string>
{
    public EditLyricsPopup(string initialLyrics, string defaultTitle, string defaultArtist)
    {
        InitializeComponent();

        BindingContext = new EditLyricsForm(this, initialLyrics, defaultTitle, defaultArtist);

#if WINDOWS
        LyricsEditor.FontFamily = "Consolas";
#endif
    }
}

public partial class EditLyricsForm : ObservableObject
{
    private readonly Popup<string> _popup;

    [ObservableProperty] public partial string LyricsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchArtist { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchType { get; set; } = "Timed Lyrics (LRC)";

    [ObservableProperty] public partial bool IsAdvancedOpen { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }

    public EditLyricsForm(Popup<string> popup, string initialLyrics, string defaultTitle, string defaultArtist)
    {
        _popup = popup;
        LyricsText = initialLyrics;
        SearchTitle = defaultTitle;
        SearchArtist = defaultArtist;
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen;

    [RelayCommand]
    private async Task SearchOnlineAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchTitle)) return;
        IsSearching = true;

        try
        {
            string query = string.IsNullOrWhiteSpace(SearchArtist) ? SearchTitle : $"{SearchTitle} {SearchArtist}";
            bool wantTimed = SearchType == "Timed Lyrics (LRC)";

            string? foundLyrics = await LyricsClient.SearchManualAsync(query, wantTimed);

            if (!string.IsNullOrWhiteSpace(foundLyrics))
            {
                LyricsText = foundLyrics;
                IsAdvancedOpen = false;
            }
            else
            {
                var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;
                if (activePage != null)
                    await activePage.DisplayAlertAsync("Not Found", "Could not find lyrics matching these filters.", "OK");
            }
        }
        catch (Exception ex)
        {
            var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            if (activePage != null)
                await activePage.DisplayAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task Cancel() => await _popup.CloseAsync(null!);

    [RelayCommand]
    private async Task Save() => await _popup.CloseAsync(LyricsText);
}