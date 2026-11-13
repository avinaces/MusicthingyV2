using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Optional URL of a PO-token provider server (bgutil-ytdlp-pot-provider
/// style, e.g. http://192.168.1.10:4416).
///
/// v8.0 precedence for GVS PO tokens: a manually imported ***PO TOKEN***
/// pair first, then the BUILT-IN on-device generator (Android — see the
/// "PO Token Generator (built-in)" setting), and only THEN this URL (used
/// when the built-in generator is off or fails — e.g. YouTube changed the
/// BotGuard challenge, or you are on Windows).
///
/// This is device-local configuration, NOT account auth: it is pushed
/// straight to the resolver, survives account switches, and never enters
/// the OpenTune ***FIELD*** import/export block.
///
/// Run the provider on any machine the phone can reach:
///   docker run --rm -p 4416:4416 brainicism/bgutil-ytdlp-pot-provider
/// </summary>
public class PoTokenProviderUrlSetting : BaseSetting<string>
{
    public override string Key => "PoTokenProviderUrl";
    public override string Title => "PO Token Provider";
    // General (visible, editable) — unlike the legacy auth fields this is a
    // device-local utility: the user must be able to type the provider URL
    // in Settings (the Hidden category never renders a row).
    public override SettingCategory Category => SettingCategory.General;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "shield_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set (optional). The built-in generator (the setting above) already mints tokens on-device; this URL is only a fallback for when it is off or fails — bgutil-ytdlp-pot-provider, e.g. http://192.168.1.10:4416.";
        return $"PO token provider: {Value} — used when the built-in generator is off or fails (manual ***PO TOKEN*** pair still wins over both).";
    }

    protected override void OnValueChanged(string newValue)
    {
        // Device-local config: pushed DIRECTLY to the resolver (NOT through
        // AccountsAuthBridge — the AccountManager does not own this field,
        // and PushActiveYouTubeToResolver never touches it, so the push
        // survives account switches and SettingsManager.LoadAll()).
        YouTubeResolverService.PoTokenProviderUrl = newValue;
    }
}
