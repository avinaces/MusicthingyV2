using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

public class PauseOnAudioDeviceDisconnectSetting : BaseSetting<bool>
{
    public override string Key => "PauseOnAudioDeviceDisconnect";
    public override string Title => "Pause When Audio Device Disconnects";
    public override string Icon => "headphones_icon.png";
    protected override string GetBaseDescription() => "Pause playback when headphones or a Bluetooth speaker disconnect.";
    public override SettingCategory Category => SettingCategory.Playback;
    public override bool DefaultValue => true;
}
