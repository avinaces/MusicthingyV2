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

    // Menu Items
    public static readonly BindableProperty MenuItemsProperty = BindableProperty.Create(nameof(MenuItems), typeof(IEnumerable<ContextMenuItem>), typeof(ContextMenuSheet));
    public IEnumerable<ContextMenuItem> MenuItems { get => (IEnumerable<ContextMenuItem>)GetValue(MenuItemsProperty); set => SetValue(MenuItemsProperty, value); }

    public Action<object?>? CloseAction { get; set; }

    public ContextMenuSheet()
    {
        InitializeComponent();

        // Wire up the interface action so SlideUpSheetHost can natively trigger a close when tapping the dark background
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
/// Represents a single actionable item within the ContextMenuSheet.
/// </summary>
public class ContextMenuItem
{
    public string Title { get; set; } = string.Empty;
    public string IconSource { get; set; } = string.Empty;
    public bool IsDestructive { get; set; }
    public Action? Action { get; set; }

    private ICommand? _itemCommand;
    /// <summary>
    /// The command bound to the native Button that executes the provided Action.
    /// </summary>
    public ICommand ItemCommand => _itemCommand ??= new RelayCommand(() => Action?.Invoke());

    // Muted, softer red for destructive actions to blend better with the dark theme.
    public Color TextColor => IsDestructive ? ThemeResources.Color("Danger") : ThemeResources.Color("TextColor");
}
