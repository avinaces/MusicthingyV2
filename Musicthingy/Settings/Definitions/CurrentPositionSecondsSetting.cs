using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class CurrentPositionSecondsSetting : BaseSetting<double>
{
    public override string Key => "CurrentPositionSeconds";
    public override string Title => "Current Position Seconds";
    protected override string GetBaseDescription() => "Saved current playback position in seconds.";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override double DefaultValue => 0.0;
}
