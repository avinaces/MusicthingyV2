using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Musicthingy.Views;

public partial class EditTagsPopup : Popup<object>
{
    public EditTagsForm Form { get; }

    public EditTagsPopup(List<string> currentTags, List<string> allKnownTags)
    {
        // FIX: Initialize the Form and DataContext *before* calling InitializeComponent 
        // so that the XAML bindings can correctly resolve the Form property.
        Form = new EditTagsForm(currentTags, allKnownTags);
        BindingContext = Form;

        InitializeComponent();

#if ANDROID
        var displayInfo = DeviceDisplay.Current.MainDisplayInfo;
        double logicalWidth = displayInfo.Width / displayInfo.Density;
        double logicalHeight = displayInfo.Height / displayInfo.Density;

        // The UI elements exist now because we added x:Name="RootBorder" back to the XAML
        RootBorder.WidthRequest = logicalWidth * 0.949;
        RootBorder.MaximumHeightRequest = logicalHeight * 0.899;
#endif
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(null);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var finalTags = Form.AllTags
            .Where(t => t.IsSelected)
            .Select(t => t.Name)
            .ToList();

        await CloseAsync(finalTags);
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

    // We can only create a tag if there's text, and it doesn't already exactly match an existing tag
    public bool CanCreateTag =>
        !string.IsNullOrWhiteSpace(SearchText) &&
        !AllTags.Any(t => t.Name.Equals(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    public EditTagsForm(List<string> currentTags, List<string> allKnownTags)
    {
        // Combine all tags, ensuring the currently assigned ones are definitely in the list, then sort alphabetically
        var combinedTags = allKnownTags
            .Union(currentTags, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        foreach (var tag in combinedTags)
        {
            AllTags.Add(new SelectableTag
            {
                Name = tag,
                IsSelected = currentTags.Contains(tag, StringComparer.OrdinalIgnoreCase)
            });
        }

        FilterTags();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterTags();
    }

    private void FilterTags()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            DisplayTags = new ObservableCollection<SelectableTag>(AllTags);
        }
        else
        {
            var lowerSearch = SearchText.ToLowerInvariant();
            DisplayTags = new ObservableCollection<SelectableTag>(
                AllTags.Where(t => t.Name.ToLowerInvariant().Contains(lowerSearch))
            );
        }
    }

    [RelayCommand]
    private void ToggleTag(SelectableTag tag)
    {
        tag.IsSelected = !tag.IsSelected;
    }

    [RelayCommand]
    private void CreateTag()
    {
        if (!CanCreateTag) return;

        var newTagName = SearchText.Trim();
        var newTag = new SelectableTag { Name = newTagName, IsSelected = true };

        // Insert at the top so the user immediately sees it
        AllTags.Insert(0, newTag);

        // Clearing the search text auto-triggers the FilterTags() method via OnSearchTextChanged
        SearchText = string.Empty;

        SaveToGlobalCache(newTagName);
    }

    private void SaveToGlobalCache(string newTagName)
    {
        try
        {
            // Store it globally so it's remembered even if the user backs out without saving the track
            var storedTagsJson = Preferences.Default.Get("AppGlobalTags", "[]");
            var stored = JsonSerializer.Deserialize<List<string>>(storedTagsJson) ?? new List<string>();

            if (!stored.Contains(newTagName, StringComparer.OrdinalIgnoreCase))
            {
                stored.Add(newTagName);
                Preferences.Default.Set("AppGlobalTags", JsonSerializer.Serialize(stored));
            }
        }
        catch { /* Ignore serialization issues */ }
    }
}

public partial class SelectableTag : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // When IsSelected is toggled, alert the UI that the color getters also changed
    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(TextColor));
    }

    public Color BackgroundColor => IsSelected ? Color.FromArgb("#2d5b8c") : Color.FromArgb("#161b22");
    public Color BorderColor => IsSelected ? Color.FromArgb("#4d7bb0") : Color.FromArgb("#30363d");
    public Color TextColor => IsSelected ? Colors.White : Color.FromArgb("#c9d1d9");
}