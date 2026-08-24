using Musicthingy.Services;
using System.Diagnostics;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Action setting that clears all YouTube auth data (cookies, visitorData,
/// dataSyncId, poToken, account info). Useful for logging out or resetting.
/// </summary>
public class ClearYouTubeAuthSetting : ActionSetting
{
    // Lazily resolved at call time to avoid circular dependency.
    private readonly IServiceProvider _services;

    public override string Key => "ClearYouTubeAuth";
    public override string Title => "Clear YouTube Account";
    public override SettingCategory Category => SettingCategory.General;
    //public override string Icon => "log_out_icon.png";

    private SettingsManager? SettingsManager => _services.GetService<SettingsManager>();

    protected override string GetBaseDescription()
    {
        var sm = SettingsManager;
        if (sm == null)
            return "No account linked.";

        var cookie = sm.GetAllSettings()
            .OfType<InnerTubeCookieSetting>()
            .FirstOrDefault();

        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
            return "No account linked.";
        return "Remove all YouTube auth data. Anonymous-only playback will resume.";
    }

    public ClearYouTubeAuthSetting(IServiceProvider services)
    {
        _services = services;
    }

    public override async void ExecuteAction()
    {
        try
        {
            var page = Application.Current?.Windows?.FirstOrDefault()?.Page;
            if (page == null) return;

            bool confirm = await page.DisplayAlertAsync(
                "Clear YouTube Account",
                "This will remove all stored cookies, visitor data, and PO tokens.\n" +
                "Restricted videos will no longer play until you re-import.\n\n" +
                "Continue?",
                "Clear", "Cancel");

            if (!confirm) return;

            var sm = SettingsManager;
            if (sm == null) return;
            var settings = sm.GetAllSettings().ToList();

            settings.OfType<InnerTubeCookieSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<VisitorDataSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<DataSyncIdSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<PoTokenSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<AccountNameSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<AccountEmailSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            settings.OfType<AccountChannelHandleSetting>().FirstOrDefault()?.Let(s => s.Value = "");

            // Also clear the resolver's static state directly
            YouTubeResolverService.Cookies = null;
            YouTubeResolverService.VisitorData = null;
            YouTubeResolverService.DataSyncId = null;
            YouTubeResolverService.PoToken = null;

            // Refresh descriptions
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnPropertyChanged(nameof(Description));
                // Also refresh the Import setting's description
                settings.OfType<ImportYouTubeAuthSetting>().FirstOrDefault()
                    ?.RefreshDescription();
            });

            await page.DisplayAlertAsync("Done", "YouTube auth data cleared.", "OK");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClearAuth] Error: {ex.Message}");
        }
    }
}
