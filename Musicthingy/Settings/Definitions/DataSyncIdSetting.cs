using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube dataSyncId for authenticated session continuity.
/// Format matches OpenTune's "***DATASYNC ID***" field exactly.
///
/// Optional — used by YouTube to maintain session state across requests.
/// When paired with cookies, improves reliability of the WEB client fallback.
///
/// Example value:
///   112954081425229850435
/// </summary>
public class DataSyncIdSetting : BaseSetting<string>
{
    public override string Key => "DataSyncId";
    public override string Title => "DataSync ID";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "key_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set. Optional — improves authenticated session continuity.";
        return "dataSyncId. Used with cookies for session continuity.";
    }

    protected override void OnValueChanged(string newValue)
    {
        YouTubeResolverService.DataSyncId = newValue;
    }
}
