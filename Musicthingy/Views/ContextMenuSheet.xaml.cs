using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Musicthingy.Views;

/// <summary>
/// A reusable Material Design 3 style bottom sheet menu for displaying contextual actions.
/// </summary>
public partial class ContextMenuSheet : ContentView, IBottomSheet
{
    public static readonly BindableProperty MenuTitleProperty = BindableProperty.Create(nameof(MenuTitle), typeof(string), typeof(ContextMenuSheet));
    public string MenuTitle { get => (string)GetValue(MenuTitleProperty); set => SetValue(MenuTitleProperty, value); }

    public static readonly BindableProperty MenuItemsProperty = BindableProperty.Create(nameof(MenuItems), typeof(IEnumerable<ContextMenuItem>), typeof(ContextMenuSheet));
    public IEnumerable<ContextMenuItem> MenuItems { get => (IEnumerable<ContextMenuItem>)GetValue(MenuItemsProperty); set => SetValue(MenuItemsProperty, value); }

    public Action<object?>? CloseAction { get; set; }

    public ContextMenuSheet()
    {
        InitializeComponent();

        // Wire up the interface action so SlideUpSheetHost can natively trigger a close when tapping the dark background
        CloseAction = _ => _ = CloseSheetAsync();
    }

    private async void OnMenuItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject bindable && bindable.BindingContext is ContextMenuItem item)
        {
            await CloseSheetAsync();
            item.Action?.Invoke();
        }
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
/// Represents a single actionable item within the ContextMenuSheet.
/// </summary>
public class ContextMenuItem
{
    public string Title { get; set; } = string.Empty;
    public string IconSource { get; set; } = string.Empty;
    public bool IsDestructive { get; set; }
    public Action? Action { get; set; }

    public Color TextColor => IsDestructive ? Color.FromArgb("#d73a49") : Color.FromArgb("#c9d1d9");
}