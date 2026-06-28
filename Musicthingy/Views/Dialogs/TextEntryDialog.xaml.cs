using CommunityToolkit.Maui.Views;
using Musicthingy.Settings;

namespace Musicthingy.Views.Dialogs;

public partial class TextEntryDialog : Popup
{
    private readonly ISetting _setting;

    public TextEntryDialog(ISetting setting)
    {
        InitializeComponent();
        _setting = setting;
        BindingContext = setting;

        if (setting.DialogType == "Number") InputEntry.Keyboard = Keyboard.Numeric;
    }

    private async void OnResetClicked(object sender, EventArgs e)
    {
        _setting.Reset();
        await CloseAsync();
    }

    private async void OnCancelClicked(object sender, EventArgs e) => await CloseAsync();

    private async void OnOkClicked(object sender, EventArgs e)
    {
        _setting.DisplayValue = InputEntry.Text;
        await CloseAsync();
    }
}