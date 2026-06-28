using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Musicthingy.Settings;
using Musicthingy.Views.Dialogs;

namespace Musicthingy.Views.Templates;

public partial class DialogTemplateView : ContentView
{
    public DialogTemplateView() { InitializeComponent(); }

    private void OnTapped(object sender, TappedEventArgs e)
    {
        if (BindingContext is ISetting setting)
        {
            Popup dialog = setting.DialogType switch
            {
                "ColorPicker" => new ColorPickerDialog(setting),
                "Picker" => new EnumPickerDialog(setting),
                _ => new TextEntryDialog(setting)
            };

            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            page?.ShowPopup(dialog);
        }
    }
}