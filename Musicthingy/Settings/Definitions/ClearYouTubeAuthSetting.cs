using CommunityToolkit.Maui.Views;
using Musicthingy.Services;
using Musicthingy.Services.Accounts;
using System.Diagnostics;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Action setting that clears ALL YouTube auth: the multi-account store
/// (OAuth tokens + cookie accounts) and the legacy hidden settings.
/// Anonymous-only playback resumes afterwards.
/// </summary>
public class ClearYouTubeAuthSetting : ActionSetting
{
    // Lazily resolved at call time to avoid circular dependency.
    private readonly IServiceProvider _services;

    public override string Key => "ClearYouTubeAuth";
    public override string Title => "Clear YouTube Account";
    public override SettingCategory Category => SettingCategory.General;
    // App icon convention: "<lucide-name>_icon.png" (lucide "log-out").
    public override string Icon => "log_out_icon.png";

    private SettingsManager? SettingsManager => _services.GetService<SettingsManager>();
    private AccountManager? Accounts => _services.GetService<AccountManager>();

    protected override string GetBaseDescription()
    {
        var accounts = Accounts;
        if (accounts != null && accounts.Count(MusicPlatform.YouTube) > 0)
        {
            var active = accounts.GetActive(MusicPlatform.YouTube);
            return $"Remove ALL accounts ({accounts.Count(MusicPlatform.YouTube)} linked" +
                   (active != null ? $", active: {active.DisplayName}" : "") +
                   "). Anonymous-only playback will resume.";
        }

        var sm = SettingsManager;
        var cookie = sm?.GetAllSettings().OfType<InnerTubeCookieSetting>().FirstOrDefault();
        if (cookie != null && !string.IsNullOrWhiteSpace(cookie.Value))
            return "Remove all YouTube auth data. Anonymous-only playback will resume.";

        return "No account linked.";
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
                "Clear YouTube Accounts",
                "This will remove ALL linked YouTube accounts (OAuth tokens and cookies)\n" +
                "plus the legacy cookie fields.\n\n" +
                "Restricted videos will no longer play until you sign in again.\n\n" +
                "Continue?",
                "Clear", "Cancel");

            if (!confirm) return;

            // 1. Clear the account store + resolver state.
            var accounts = Accounts;
            if (accounts != null)
                await accounts.ClearPlatformAsync(MusicPlatform.YouTube);

            // 2. Clear the legacy hidden settings (rollbacks / old installs).
            var sm = SettingsManager;
            var settings = sm?.GetAllSettings().ToList();
            if (settings != null)
            {
                settings.OfType<InnerTubeCookieSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<VisitorDataSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<DataSyncIdSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<PoTokenSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<AccountNameSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<AccountEmailSetting>().FirstOrDefault()?.Let(s => s.Value = "");
                settings.OfType<AccountChannelHandleSetting>().FirstOrDefault()?.Let(s => s.Value = "");
            }

            // 3. Belt and braces: clear the resolver directly too.
            Services.YouTubeResolverService.Cookies = null;
            Services.YouTubeResolverService.VisitorData = null;
            Services.YouTubeResolverService.DataSyncId = null;
            Services.YouTubeResolverService.PoToken = null;
            Services.YouTubeResolverService.OAuthTokenProvider = null;

            // Refresh descriptions
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OnPropertyChanged(nameof(Description));
                settings?.OfType<ImportYouTubeAuthSetting>().FirstOrDefault()
                    ?.RefreshDescription();
            });

            await page.DisplayAlertAsync("Done", "All YouTube auth data cleared.", "OK");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClearAuth] Error: {ex.Message}");
        }
    }
}
