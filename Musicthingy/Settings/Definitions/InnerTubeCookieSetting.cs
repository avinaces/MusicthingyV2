using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube InnerTube cookie string (semicolon-separated header format).
/// Format matches OpenTune's "***INNERTUBE COOKIE***" field exactly so users
/// can copy-paste between apps.
///
/// Example value:
///   __Secure-3PSIDTS=sidts-...; SSID=...; APISID=...; SAPISID=...;
///   __Secure-3PAPISID=...; SID=...; __Secure-1PSID=...; __Secure-3PSID=...;
///   LOGIN_INFO=...; VISITOR_INFO1_LIVE=...
///
/// These are sensitive (full account access). Stored via Preferences because
/// the existing settings system uses it, but consider migrating to SecureStorage.
/// </summary>
public class InnerTubeCookieSetting : BaseSetting<string>
{
    public override string Key => "InnerTubeCookie";
    public override string Title => "InnerTube Cookie";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "lock_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set. Anonymous-only playback (some videos won't play).";
        return "YouTube session cookies. Used for WEB client fallback on restricted videos.";
    }

    protected override void OnValueChanged(string newValue)
    {
        // Push to the resolver's static auth state
        YouTubeResolverService.Cookies = newValue;
    }
}
