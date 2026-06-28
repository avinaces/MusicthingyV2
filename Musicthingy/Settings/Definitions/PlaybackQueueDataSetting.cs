using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class PlaybackQueueDataSetting : BaseSetting<string>
{
    public override string Key => "PlaybackQueueData";
    public override string Title => "Playback Queue Data";
    protected override string GetBaseDescription() => "Serialized playback queue state.";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => string.Empty;
}
