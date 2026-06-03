using Microsoft.Maui.Controls;
using Musicthingy.ViewModels;
using System;
using System.Threading.Tasks;

namespace Musicthingy.Views;

public partial class SortOptionsSheet : ContentView, IBottomSheet
{
    public Action<object?>? CloseAction { get; set; }
    private readonly MainViewModel _vm;

    public SortOptionsSheet(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // CRITICAL FIX: Properly connect CloseAction so SlideUpSheetHost natively hides it
        CloseAction = _ => _ = CloseSheetAsync();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // Triggered automatically by BottomSheetContainer when swiping down or tapping background
        _ = CloseSheetAsync();
    }

    private async Task CloseSheetAsync()
    {
        if (Services.SheetManager.Host != null)
        {
            await Services.SheetManager.Host.HideAsync(this);
        }
    }

    private void OnSortFieldClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && Enum.TryParse<SortField>(btn.CommandParameter?.ToString(), out var field))
        {
            _vm.SetSortFieldCommand.Execute(field);
        }

        _ = CloseSheetAsync();
    }
}