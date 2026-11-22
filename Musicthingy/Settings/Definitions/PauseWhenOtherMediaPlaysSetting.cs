using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

public class PauseWhenOtherMediaPlaysSetting : BaseSetting<bool>
{
    public override string Key => "PauseWhenOtherMediaPlays";
    public override string Title => "Pause When Other Media Plays";
    public override string Icon => "audio_waveform_icon.png";
    protected override string GetBaseDescription() => "Pause playback when another app starts playing audio.";
    public override SettingCategory Category => SettingCategory.Playback;
    public override bool DefaultValue => true;
}
