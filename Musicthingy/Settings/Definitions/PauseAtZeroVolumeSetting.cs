using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

public class PauseAtZeroVolumeSetting : BaseSetting<bool>
{
    public override string Key => "PauseAtZeroVolume";
    public override string Title => "Pause at Zero Volume";
    public override string Icon => "volume_icon.png";
    protected override string GetBaseDescription() => "Pause playback when the volume is zero.";
    public override SettingCategory Category => SettingCategory.Playback;
    public override bool DefaultValue => false;
}