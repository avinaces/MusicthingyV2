using Musicthingy.Services.PoToken;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// Master switch of the BUILT-IN, on-device GVS PO token generator (v8.0).
///
/// Android: a hidden WebView runs the youtube.com web player's own BotGuard
/// attestation flow (challenge from the page → interpreter VM → snapshot →
/// GenerateIT → WebPoMinter) and mints content-bound (video ID) GVS PO
/// tokens locally on the phone — no external server, no manual import, no
/// bgutil docker container. Tokens are attached to SABR streaming requests,
/// which lifts YouTube's ~60-second unattested-stream cutoff AND unlocks
/// seeking/skipping on SABR-only tracks (any non-zero start offset requires
/// attestation immediately — a fresh session URL alone is not enough).
///
/// Default: ON. Windows: no on-device generator (row is informational there;
/// use the PO Token Provider URL or import a ***VISITOR DATA*** +
/// ***PO TOKEN*** pair instead).
/// </summary>
public class PoTokenBuiltInSetting : BaseSetting<bool>
{
    public override string Key => "PoTokenBuiltInGenerator";
    public override string Title => "PO Token Generator (built-in, BROKEN)";
    public override SettingCategory Category => SettingCategory.General;
    public override bool DefaultValue => false;
    public override string Icon => "shield_icon.png";

    protected override string GetBaseDescription()
    {
#if ANDROID
        return Value
            ? "On. GVS PO tokens are minted ON-DEVICE (hidden WebView running YouTube's own BotGuard attestation) and attached to SABR streams — full-length playback and seeking past the ~60s attestation cutoff."
            : "Off(Default). SABR-only videos stop at 60s, and seeking/skipping does not work.";
#else
        return "Android-only feature. On Windows, set a PO Token Provider URL (below) or import a ***VISITOR DATA*** + ***PO TOKEN*** pair (YouTube Accounts → Paste cookies).";
#endif
    }

    protected override void OnValueChanged(bool newValue)
    {
        BuiltInPoTokenProvider.IsEnabled = newValue;
    }
}
