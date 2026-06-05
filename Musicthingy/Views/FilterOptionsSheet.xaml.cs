using Microsoft.Maui.Controls;
using Musicthingy.ViewModels;
using System;
using System.Threading.Tasks;

namespace Musicthingy.Views;

public partial class FilterOptionsSheet : ContentView, IBottomSheet
{
    public Action<object?>? CloseAction { get; set; }

    public FilterOptionsSheet(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;

        CloseAction = _ => _ = CloseSheetAsync();
    }

    private void OnCloseRequested(object? sender, EventArgs e)
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