using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube account display name.
/// Format matches OpenTune's "***ACCOUNT NAME***" field exactly.
///
/// Informational only — not used for authentication, but displayed in the
/// settings UI to confirm which account is linked.
///
/// Example value:
///   user
/// </summary>
public class AccountNameSetting : BaseSetting<string>
{
    public override string Key => "YouTubeAccountName";
    public override string Title => "Account Name";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "user_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set.";
        return $"Linked account: {Value}";
    }

    protected override void OnValueChanged(string newValue) { /* informational only */ }
}
