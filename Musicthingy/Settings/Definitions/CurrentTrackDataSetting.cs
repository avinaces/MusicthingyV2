using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class CurrentTrackDataSetting : BaseSetting<string>
{
    public override string Key => "CurrentTrackData";
    public override string Title => "Current Track Data";
    protected override string GetBaseDescription() => "Serialized current track reference.";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => string.Empty;
}
