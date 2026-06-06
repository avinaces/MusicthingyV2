using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Musicthingy;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Musicthingy.Views;

/// <summary>
/// A reusable Material Design 3 style bottom sheet menu for displaying contextual actions.
/// </summary>
public partial class ContextMenuSheet : ContentView, IBottomSheet
{
    // Header Properties
    public static readonly BindableProperty HasHeaderProperty = BindableProperty.Create(nameof(HasHeader), typeof(bool), typeof(ContextMenuSheet), defaultValue: false);
    public bool HasHeader { get => (bool)GetValue(HasHeaderProperty); set => SetValue(HasHeaderProperty, value); }

    public static readonly BindableProperty HeaderTitleProperty = BindableProperty.Create(nameof(HeaderTitle), typeof(string), typeof(ContextMenuSheet));
    public string HeaderTitle { get => (string)GetValue(HeaderTitleProperty); set => SetValue(HeaderTitleProperty, value); }

    public static readonly BindableProperty HeaderSubtitleProperty = BindableProperty.Create(nameof(HeaderSubtitle), typeof(string), typeof(ContextMenuSheet));
    public string HeaderSubtitle { get => (string)GetValue(HeaderSubtitleProperty); set => SetValue(HeaderSubtitleProperty, value); }

    public static readonly BindableProperty HeaderImageSourceProperty = BindableProperty.Create(nameof(HeaderImageSource), typeof(ImageSource), typeof(ContextMenuSheet));
    public ImageSource HeaderImageSource { get => (ImageSource)GetValue(HeaderImageSourceProperty); set => SetValue(HeaderImageSourceProperty, value); }

    // Multi-group support (Allows grouped distinct cards, like an isolated Delete button)
    public static readonly BindableProperty MenuGroupsProperty = BindableProperty.Create(
        nameof(MenuGroups),
        typeof(IEnumerable<ContextMenuGroup>),
        typeof(ContextMenuSheet),
        propertyChanged: OnMenuGroupsChanged);

    public IEnumerable<ContextMenuGroup> MenuGroups
    {
        get => (IEnumerable<ContextMenuGroup>)GetValue(MenuGroupsProperty);
        set => SetValue(MenuGroupsProperty, value);
    }

    private static void OnMenuGroupsChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (newValue is IEnumerable<ContextMenuGroup> groups)
        {
            foreach (ContextMenuGroup group in groups)
            {
                // Auto-hide the separator line for the last item in every group block
                for (int i = 0; i < group.Count; i++)
                {
                    group[i].IsSeparatorVisible = (i < group.Count - 1);
                }
            }
        }
    }

    // Legacy fallback property: automatically wraps a single list into a group to prevent breaking existing usages
    public static readonly BindableProperty MenuItemsProperty = BindableProperty.Create(
        nameof(MenuItems),
        typeof(IEnumerable<ContextMenuItem>),
        typeof(ContextMenuSheet),
        propertyChanged: (b, o, n) =>
        {
            if (b is ContextMenuSheet sheet && n is IEnumerable<ContextMenuItem> items)
            {
                sheet.MenuGroups = new List<ContextMenuGroup> { new ContextMenuGroup(items) };
            }
        });

    public IEnumerable<ContextMenuItem> MenuItems
    {
        get => (IEnumerable<ContextMenuItem>)GetValue(MenuItemsProperty);
        set => SetValue(MenuItemsProperty, value);
    }

    public Action<object?>? CloseAction { get; set; }

    public ContextMenuSheet()
    {
        InitializeComponent();
        CloseAction = _ => _ = CloseSheetAsync();
    }

    private void OnSheetCloseRequested(object? sender, EventArgs e)
    {
        _ = CloseSheetAsync();
    }

    private async Task CloseSheetAsync()
    {
        if (Services.SheetManager.Host != null)
        {
            await Services.SheetManager.Host.HideAsync(this);
        }
    }
}

/// <summary>
/// A collection of contextual items meant to be displayed together inside a single visual container.
/// </summary>
public class ContextMenuGroup : List<ContextMenuItem>
{
    public ContextMenuGroup() { }
    public ContextMenuGroup(IEnumerable<ContextMenuItem> items) : base(items) { }
}

/// <summary>
/// Represents a single actionable item within the ContextMenuSheet.
/// </summary>
public class ContextMenuItem : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string IconSource { get; set; } = string.Empty;
    public bool IsDestructive { get; set; }
    public Action? Action { get; set; }

    // Automatically managed by the MenuGroups dependency property setter
    private bool _isSeparatorVisible = true;
    public bool IsSeparatorVisible
    {
        get => _isSeparatorVisible;
        set => SetProperty(ref _isSeparatorVisible, value);
    }

    private ICommand? _itemCommand;
    public ICommand ItemCommand => _itemCommand ??= new RelayCommand(() => Action?.Invoke());

    public Color TextColor => IsDestructive ? ThemeResources.Color("Danger") : ThemeResources.Color("TextColor");
}