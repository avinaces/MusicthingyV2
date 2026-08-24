using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube account email.
/// Format matches OpenTune's "***ACCOUNT EMAIL***" field exactly.
///
/// Informational only — not used for authentication.
///
/// Example value:
///   user@gmail.com
/// </summary>
public class AccountEmailSetting : BaseSetting<string>
{
    public override string Key => "YouTubeAccountEmail";
    public override string Title => "Account Email";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "mail_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set.";
        return $"Email: {Value}";
    }

    protected override void OnValueChanged(string newValue) { /* informational only */ }
}
