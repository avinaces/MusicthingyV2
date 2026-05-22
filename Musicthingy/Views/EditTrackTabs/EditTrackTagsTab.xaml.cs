using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Musicthingy;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace Musicthingy.Views;

public partial class EditTrackTagsTab : ContentView
{
    public EditTrackTagsTab()
    {
        InitializeComponent();
    }
}

public partial class EditTagsForm : ObservableObject
{
    public ObservableCollection<SelectableTag> AllTags { get; } = new();

    [ObservableProperty]
    public partial ObservableCollection<SelectableTag> DisplayTags { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateTag))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool CanCreateTag =>
        !string.IsNullOrWhiteSpace(SearchText) &&
        !AllTags.Any(t => t.Name.Equals(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    public EditTagsForm(List<string> currentTags, List<string> allKnownTags)
    {
        List<string> combinedTags = allKnownTags
            .Union(currentTags, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        foreach (string tag in combinedTags)
        {
            AllTags.Add(new SelectableTag
            {
                Name = tag,
                IsSelected = currentTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
            });
        }
        FilterTags();
    }

    partial void OnSearchTextChanged(string value) => FilterTags();

    private void FilterTags()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            DisplayTags = new ObservableCollection<SelectableTag>(AllTags);
        }
        else
        {
            string lowerSearch = SearchText.ToLowerInvariant();
            DisplayTags = new ObservableCollection<SelectableTag>(
                AllTags.Where(t => t.Name.ToLowerInvariant().Contains(lowerSearch))
            );
        }
    }

    [RelayCommand]
    private void ToggleTag(SelectableTag tag) => tag.IsSelected = !tag.IsSelected;

    [RelayCommand]
    private void CreateTag()
    {
        if (!CanCreateTag) return;

        string newTagName = SearchText.Trim();
        SelectableTag newTag = new SelectableTag { Name = newTagName, IsSelected = true };

        AllTags.Insert(0, newTag);
        SearchText = string.Empty;

        SaveToGlobalCache(newTagName);
    }

    private void SaveToGlobalCache(string newTagName)
    {
        try
        {
            string storedTagsJson = Preferences.Default.Get("AppGlobalTags", "[]");
            List<string> stored = JsonSerializer.Deserialize<List<string>>(storedTagsJson) ?? new List<string>();

            if (!stored.Contains(newTagName, StringComparer.OrdinalIgnoreCase))
            {
                stored.Add(newTagName);
                Preferences.Default.Set("AppGlobalTags", JsonSerializer.Serialize(stored));
            }
        }
        catch { }
    }
}

public partial class SelectableTag : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(TextColor));
    }

    public Color BackgroundColor => IsSelected ? ThemeResources.Color("AccentFill") : ThemeResources.Color("CardColor");
    public Color BorderColor => IsSelected ? ThemeResources.Color("AccentOutline") : ThemeResources.Color("BorderColor");
    public Color TextColor => IsSelected ? ThemeResources.Color("OnAccent") : ThemeResources.Color("TextColor");
}
