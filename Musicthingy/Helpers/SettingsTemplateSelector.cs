using Musicthingy.Settings;
using Musicthingy.Views;
using Musicthingy.Views.Templates;

namespace Musicthingy.Helpers;

public class SettingsTemplateSelector : DataTemplateSelector
{
    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is ISetting setting)
        {
            return setting.TemplateName switch
            {
                "SwitchTemplate" => new DataTemplate(typeof(SwitchTemplateView)), // Has standard MAUI <Switch>
                "ActionTemplate" => new DataTemplate(typeof(ActionTemplateView)), // Binds directly to ICommand
                _ => new DataTemplate(typeof(DialogTemplateView))
            };
        }
        return new DataTemplate(typeof(DialogTemplateView));
    }
}