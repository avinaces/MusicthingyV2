using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Musicthingy;
using Musicthingy.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Musicthingy.Views;

public partial class EditTrackSheet : ContentView, IBottomSheet
{
    public Action<object?>? CloseAction { get; set; }

    private readonly PlaylistItem _originalTrack;
    private readonly List<string> _allTags;

    public EditTrackSheet(PlaylistItem track, List<string> allTags)
    {
        InitializeComponent();

        _originalTrack = track;
        _allTags = allTags;

        // Assign the isolated forms to each tab
        GeneralTab.BindingContext = new EditTrackForm(track);
        LyricsTab.BindingContext = new EditLyricsForm(track.Lyrics ?? "", track.Title, track.Artist ?? "", track.Url);
        TagsTab.BindingContext = new EditTagsForm(track.Tags.ToList(), allTags);
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (int.TryParse(e.Parameter?.ToString(), out int tabIndex))
        {
            SetActiveTab(tabIndex);
        }
    }

    private void SetActiveTab(int index)
    {
        // Smoothly toggle the pill background indicators
        TabIndicator0.IsVisible = index == 0;
        TabIndicator1.IsVisible = index == 1;
        TabIndicator2.IsVisible = index == 2;

        // Update the text contrast to match the active state
        Color activeText = ThemeResources.Color("OnSurfaceInverse");
        Color inactiveText = ThemeResources.Color("MutedColor");
        TabLabel0.TextColor = index == 0 ? activeText : inactiveText;
        TabLabel1.TextColor = index == 1 ? activeText : inactiveText;
        TabLabel2.TextColor = index == 2 ? activeText : inactiveText;

        // Swap the active content views
        GeneralTab.IsVisible = index == 0;
        LyricsTab.IsVisible = index == 1;
        TagsTab.IsVisible = index == 2;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => CloseAction?.Invoke(null);
    private void OnCancelClicked(object? sender, EventArgs e) => CloseAction?.Invoke(false);

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        EditTrackForm generalForm = (EditTrackForm)GeneralTab.BindingContext;
        EditLyricsForm lyricsForm = (EditLyricsForm)LyricsTab.BindingContext;
        EditTagsForm tagsForm = (EditTagsForm)TagsTab.BindingContext;

        // Apply General Metadata
        _originalTrack.Title = generalForm.EditTitle ?? string.Empty;
        _originalTrack.Url = generalForm.EditUrl ?? string.Empty;
        _originalTrack.IconUrl = generalForm.EditIconUrl;

        if (int.TryParse(generalForm.EditStartTime, out int start)) _originalTrack.StartTime = start;
        else _originalTrack.StartTime = null;

        if (int.TryParse(generalForm.EditEndTime, out int end)) _originalTrack.EndTime = end;
        else _originalTrack.EndTime = null;

        // Apply Lyrics
        _originalTrack.Lyrics = lyricsForm.LyricsText;

        // Apply Tags
        _originalTrack.Tags.Clear();
        List<string> selectedTags = tagsForm.AllTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();

        foreach (string tag in selectedTags)
        {
            _originalTrack.Tags.Add(tag);
            if (!_allTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _allTags.Add(tag);
            }
        }

        CloseAction?.Invoke(true);
    }
}
