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

    // Added optional initialTabIndex parameter (0 = Track, 1 = Lyrics, 2 = Tags)
    public EditTrackSheet(PlaylistItem track, List<string> allTags, int initialTabIndex = 0)
    {
        InitializeComponent();

        _originalTrack = track;
        _allTags = allTags;

        // Assign the isolated forms to each tab
        GeneralTab.BindingContext = new EditTrackForm(track);
        LyricsTab.BindingContext = new EditLyricsForm(track.Lyrics ?? "", track.Title, track.Artist ?? "", track.Url);
        TagsTab.BindingContext = new EditTagsForm(track.Tags.ToList(), allTags);

        // Pre-select the requested tab
        TabBar.SelectedIndex = initialTabIndex;
        SetActiveTab(initialTabIndex);
    }

    private void OnTabChanged(object? sender, int index)
    {
        SetActiveTab(index);
    }

    private void SetActiveTab(int index)
    {
        // Color updates and pills are handled natively by the component now
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