using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using Musicthingy.Models;
using System.Collections.ObjectModel;

namespace Musicthingy.Views;

public partial class EditTrackPopup : Popup<bool>
{
    private readonly PlaylistItem _originalTrack;
    private readonly EditTrackForm _form;

    public EditTrackPopup(PlaylistItem track)
    {
        InitializeComponent();

        _originalTrack = track;

        _form = new EditTrackForm
        {
            EditTitle = track.Title,
            EditUrl = track.Url,
            EditStartTime = track.StartTime?.ToString() ?? "",
            EditEndTime = track.EndTime?.ToString() ?? "",
            EditLyrics = track.Lyrics ?? "",

            ItemIconSource = track.ItemIconSource,
            LastPlayedText = track.LastPlayedAt?.ToString("M/dd/yyyy, h:mm:ss tt") ?? "",
            Tags = new ObservableCollection<string>(track.Tags)
        };

        BindingContext = _form;

#if ANDROID || IOS
        var displayInfo = Microsoft.Maui.Devices.DeviceDisplay.Current.MainDisplayInfo;
        double logicalWidth = displayInfo.Width / displayInfo.Density;
        double logicalHeight = displayInfo.Height / displayInfo.Density;

        // Set to act as a large centered native dialog (avoids clipping into notches/safe areas entirely)
        RootBorder.WidthRequest = logicalWidth * 0.92;
        RootBorder.MaximumHeightRequest = logicalHeight * 0.85;
#endif
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(false);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        _originalTrack.Title = _form.EditTitle ?? string.Empty;
        _originalTrack.Url = _form.EditUrl ?? string.Empty;
        _originalTrack.Lyrics = _form.EditLyrics;

        if (int.TryParse(_form.EditStartTime, out int start)) _originalTrack.StartTime = start;
        else _originalTrack.StartTime = null;

        if (int.TryParse(_form.EditEndTime, out int end)) _originalTrack.EndTime = end;
        else _originalTrack.EndTime = null;

        _originalTrack.Tags.Clear();
        foreach (var tag in _form.Tags) _originalTrack.Tags.Add(tag);

        await CloseAsync(true);
    }

    private async void OnEditLyricsClicked(object? sender, EventArgs e)
    {
        var lyricsPopup = new EditLyricsPopup(_form.EditLyrics, _form.EditTitle, _originalTrack.Artist ?? "");
        var activePage = Shell.Current ?? Application.Current?.Windows.FirstOrDefault()?.Page;

        if (activePage != null)
        {
            var popupResult = await activePage.ShowPopupAsync<bool>(lyricsPopup);
            if (popupResult != null && popupResult.Result)
            {
                _form.EditLyrics = lyricsPopup.Form.LyricsText;
            }
        }
    }
}

public partial class EditTrackForm : ObservableObject
{
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty; [ObservableProperty] public partial string EditUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditStartTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditEndTime { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditLyrics { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? ItemIconSource { get; set; }
    [ObservableProperty] public partial string LastPlayedText { get; set; } = string.Empty;

    [ObservableProperty] public partial ObservableCollection<string> Tags { get; set; } = new();
}