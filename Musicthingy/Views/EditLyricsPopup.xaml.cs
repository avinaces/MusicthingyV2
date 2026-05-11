using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;

namespace Musicthingy.Views;

public partial class EditLyricsPopup : Popup<bool>
{
    public EditLyricsForm Form { get; }

    public EditLyricsPopup(string initialLyrics, string defaultTitle, string defaultArtist)
    {
        InitializeComponent();

        Form = new EditLyricsForm(initialLyrics, defaultTitle, defaultArtist);
        BindingContext = Form;

#if WINDOWS
        LyricsEditor.FontFamily = "Consolas";
#endif

#if ANDROID || IOS
        var displayInfo = Microsoft.Maui.Devices.DeviceDisplay.Current.MainDisplayInfo;
        double logicalWidth = displayInfo.Width / displayInfo.Density;
        double logicalHeight = displayInfo.Height / displayInfo.Density;

        // Set to act as a large centered native dialog
        RootBorder.WidthRequest = logicalWidth * 0.95;
        RootBorder.MaximumHeightRequest = logicalHeight * 0.85;
#endif
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(false);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        await CloseAsync(true);
    }
}

public partial class EditLyricsForm : ObservableObject
{
    [ObservableProperty] public partial string LyricsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchArtist { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchType { get; set; } = "Timed Lyrics (LRC)";

    [ObservableProperty] public partial bool IsAdvancedOpen { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }

    public EditLyricsForm(string initialLyrics, string defaultTitle, string defaultArtist)
    {
        LyricsText = initialLyrics;
        SearchTitle = defaultTitle;
        SearchArtist = defaultArtist;
    }
    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen; [RelayCommand]
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
                    await activePage.DisplayAlertAsync("Not Found", "Could not find lyrics matching these exact filters.", "OK");
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
}