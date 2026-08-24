using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube PO Token (Proof of Origin) for bot-attestation bypass.
/// Format matches OpenTune's "***PO TOKEN***" field exactly.
///
/// Optional — currently stored but not yet sent in requests. YouTube's
/// enforcement is selective as of Aug 2026; VISIONOS doesn't require it.
/// If YouTube tightens enforcement, this token will be sent in
/// context.client.serviceIntegrityDimensions.poToken and as pot= on
/// googlevideo stream URLs.
///
/// Example value:
///   ChBhpP4v4KR3CO_RzcTtfyJeIrAEIsOKXrrIOTqMup-9uClGPzb3l3qM1A1drbue...
/// </summary>
public class PoTokenSetting : BaseSetting<string>
{
    public override string Key => "PoToken";
    public override string Title => "PO Token";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "shield_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set. VISIONOS client doesn't require it (Aug 2026).";
        return "Proof-of-Origin token. Stored for future enforcement. Currently unused.";
    }

    protected override void OnValueChanged(string newValue)
    {
        YouTubeResolverService.PoToken = newValue;
    }
}
