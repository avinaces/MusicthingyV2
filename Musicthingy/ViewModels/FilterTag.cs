using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Graphics;

namespace Musicthingy.ViewModels;

public enum FilterState
{
    Neutral,
    Included,
    Excluded
}

public partial class FilterTag : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial FilterState State { get; set; } = FilterState.Neutral;

    public Color BackgroundColor => State switch
    {
        FilterState.Included => ThemeResources.Color("AccentFill"),
        FilterState.Excluded => ThemeResources.Color("DangerFill"),
        _ => ThemeResources.Color("CardColor")
    };

    public Color BorderColor => State switch
    {
        FilterState.Included => ThemeResources.Color("AccentOutline"),
        FilterState.Excluded => ThemeResources.Color("Danger"),
        _ => ThemeResources.Color("BorderColor")
    };

    public Color TextColor => State switch
    {
        FilterState.Included => ThemeResources.Color("OnAccent"),
        FilterState.Excluded => ThemeResources.Color("OnDanger"),
        _ => ThemeResources.Color("TextColor")
    };

    public bool IsIncluded => State == FilterState.Included;
    public bool IsExcluded => State == FilterState.Excluded;
    public bool IsNeutral => State == FilterState.Neutral;

    partial void OnStateChanged(FilterState value)
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IsExcluded));
        OnPropertyChanged(nameof(IsNeutral));
    }
}