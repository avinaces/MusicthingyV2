using CommunityToolkit.Maui.Views;
using Musicthingy.Settings;

namespace Musicthingy.Views.Dialogs;

public partial class EnumPickerDialog : Popup
{
    private readonly ISetting _setting;
    private string? _tempSelection;

    public EnumPickerDialog(ISetting setting)
    {
        InitializeComponent();
        _setting = setting;
        BindingContext = setting;

        OptionsList.ItemsSource = setting.Options;
        OptionsList.SelectedItem = setting.DisplayValue;
        _tempSelection = setting.DisplayValue;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _tempSelection = e.CurrentSelection.FirstOrDefault() as string;
    }

    private async void OnResetClicked(object sender, EventArgs e)
    {
        _setting.Reset();
        await CloseAsync();
    }

    private async void OnCancelClicked(object sender, EventArgs e) => await CloseAsync();

    private async void OnOkClicked(object sender, EventArgs e)
    {
        if (_tempSelection != null) _setting.DisplayValue = _tempSelection;
        await CloseAsync();
    }
}