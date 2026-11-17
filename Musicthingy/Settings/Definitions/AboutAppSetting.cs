using Musicthingy.Models;
using Musicthingy.Views;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Opens the About page: app version, source code and every bundled third-party license.
/// </summary>
public class AboutAppSetting : ActionSetting
{
    public override string Key => "AboutApp";
    public override string Title => "About & licenses";
    public override SettingCategory Category => SettingCategory.General;

    public override string Icon => "info_icon.png";

    protected override string GetBaseDescription() =>
        $"Version {LicenseCatalog.AppVersion} · all third-party licenses, readable offline";

    public override void ExecuteAction()
    {
        // The setting has no page reference, so walk the window: settings are shown as modals,
        // and pushing from the topmost modal keeps the "back" gesture popping just this page.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            Page? host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host is null)
                return;

            Page top = host.Navigation.ModalStack.Count > 0
                ? host.Navigation.ModalStack[^1]
                : host;

            await top.Navigation.PushModalAsync(new AboutPage());
        });
    }
}
