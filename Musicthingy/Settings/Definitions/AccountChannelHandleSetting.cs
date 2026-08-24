using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube channel handle (@username).
/// Format matches OpenTune's "***ACCOUNT CHANNEL HANDLE***" field exactly.
///
/// Informational only — not used for authentication.
///
/// Example value:
///   @user
/// </summary>
public class AccountChannelHandleSetting : BaseSetting<string>
{
    public override string Key => "YouTubeChannelHandle";
    public override string Title => "Channel Handle";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "at_sign_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set.";
        return $"Handle: {Value}";
    }

    protected override void OnValueChanged(string newValue) { /* informational only */ }
}
