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

        // 1. Create a "Buffer" copy
        _form = new EditTrackForm
        {
            EditTitle = track.Title,
            EditUrl = track.Url,
            EditStartTime = track.StartTime?.ToString() ?? "",
            EditEndTime = track.EndTime?.ToString() ?? "",

            ItemIconSource = track.ItemIconSource,
            LastPlayedText = track.LastPlayedAt?.ToString("M/dd/yyyy, h:mm:ss tt") ?? "",

            // Clone the tags into our form buffer
            Tags = new ObservableCollection<string>(track.Tags)
        };

        BindingContext = _form;
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(false);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        // 2. Push changes to the live track
        _originalTrack.Title = _form.EditTitle ?? string.Empty;
        _originalTrack.Url = _form.EditUrl ?? string.Empty;

        if (int.TryParse(_form.EditStartTime, out int start))
            _originalTrack.StartTime = start;
        else
            _originalTrack.StartTime = null;

        if (int.TryParse(_form.EditEndTime, out int end))
            _originalTrack.EndTime = end;
        else
            _originalTrack.EndTime = null;

        // Sync tags back to the original track
        _originalTrack.Tags.Clear();
        foreach (var tag in _form.Tags)
        {
            _originalTrack.Tags.Add(tag);
        }

        await CloseAsync(true);
    }
}

// 3. Buffer Model
public partial class EditTrackForm : ObservableObject
{
    [ObservableProperty] public partial string EditTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string EditUrl { get; set; } = string.Empty; [ObservableProperty] public partial string EditStartTime { get; set; } = string.Empty; [ObservableProperty] public partial string EditEndTime { get; set; } = string.Empty;

    [ObservableProperty] public partial ImageSource? ItemIconSource { get; set; }
    [ObservableProperty] public partial string LastPlayedText { get; set; } = string.Empty;

    // Dynamic tags list for the form
    [ObservableProperty] public partial ObservableCollection<string> Tags { get; set; } = new();
}