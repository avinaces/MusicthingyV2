using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Musicthingy;
using Musicthingy.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Musicthingy.Views;

public partial class EditPlaylistSheet : ContentView, IBottomSheet
{
    public Action<object?>? CloseAction { get; set; }

    private readonly Playlist _originalPlaylist;
    private readonly List<string> _allTags;

    public EditPlaylistSheet(Playlist playlist, List<string> allTags)
    {
        InitializeComponent();

        _originalPlaylist = playlist;
        _allTags = allTags;

        // Assign the isolated forms to each tab
        GeneralTab.BindingContext = new EditPlaylistForm(playlist);
        TagsTab.BindingContext = new EditTagsForm(playlist.Tags.ToList(), allTags);
    }

    private void OnTabChanged(object? sender, int index)
    {
        SetActiveTab(index);
    }

    private void SetActiveTab(int index)
    {
        // Color updates and pills are handled natively by the component now
        GeneralTab.IsVisible = index == 0;
        TagsTab.IsVisible = index == 1;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => CloseAction?.Invoke(null);
    private void OnCancelClicked(object? sender, EventArgs e) => CloseAction?.Invoke(false);

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        EditPlaylistForm generalForm = (EditPlaylistForm)GeneralTab.BindingContext;
        EditTagsForm tagsForm = (EditTagsForm)TagsTab.BindingContext;

        // Apply General Metadata
        _originalPlaylist.Name = generalForm.EditName ?? string.Empty;
        _originalPlaylist.Source = generalForm.EditSource ?? string.Empty;
        _originalPlaylist.IconUrl = generalForm.EditIconUrl;
        _originalPlaylist.UpdatedAt = DateTime.Now;

        // Apply Tags
        _originalPlaylist.Tags.Clear();
        List<string> selectedTags = tagsForm.AllTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();

        foreach (string tag in selectedTags)
        {
            _originalPlaylist.Tags.Add(tag);
            if (!_allTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                _allTags.Add(tag);
            }
        }

        CloseAction?.Invoke(true);
    }
}
