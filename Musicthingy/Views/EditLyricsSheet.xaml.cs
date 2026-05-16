using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musicthingy.Services;

namespace Musicthingy.Views;

public partial class EditLyricsSheet : ContentView, IBottomSheet
{
    public Action<object?>? CloseAction { get; set; }
    public EditLyricsForm Form { get; }

    public EditLyricsSheet(string initialLyrics, string defaultTitle, string defaultArtist, string trackUrl = "")
    {
        InitializeComponent();

        Form = new EditLyricsForm(initialLyrics, defaultTitle, defaultArtist, trackUrl);
        BindingContext = Form;

#if WINDOWS
        LyricsEditor.FontFamily = "Consolas";
#endif
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        CloseAction?.Invoke(false);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        CloseAction?.Invoke(true);
    }
}

public partial class EditLyricsForm : ObservableObject
{
    [ObservableProperty] public partial string LyricsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchArtist { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchType { get; set; } = "Timed Lyrics (LRC)";
    [ObservableProperty] public partial string TrackUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsAdvancedOpen { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }

    public EditLyricsForm(string initialLyrics, string defaultTitle, string defaultArtist, string trackUrl = "")
    {
        LyricsText = initialLyrics ?? string.Empty;
        SearchTitle = defaultTitle ?? string.Empty;
        SearchArtist = defaultArtist ?? string.Empty;
        TrackUrl = trackUrl ?? string.Empty;
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
            bool wantTimed = SearchType == "Timed Lyrics (LRC)";
            string? foundLyrics = await LyricsClient.FetchLyricsAsync(TrackUrl, SearchTitle, SearchArtist, wantTimed);

            if (!string.IsNullOrWhiteSpace(foundLyrics))
            {
                LyricsText = foundLyrics;
                IsAdvancedOpen = false;
            }
            else
            {
                await Shell.Current.DisplayAlertAsync("Not Found", "Could not find lyrics matching these exact filters.", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Error", ex.Message, "OK");
        }
        finally
        {
            IsSearching = false;
        }
    }
}