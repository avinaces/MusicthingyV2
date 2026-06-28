using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class RepeatModeSetting : BaseSetting<int>
{
    public override string Key => "RepeatState";
    public override string Title => "Repeat Mode";
    protected override string GetBaseDescription() => "Repeat behaviour for playback (Off/All/One).";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override int DefaultValue => 0;
}