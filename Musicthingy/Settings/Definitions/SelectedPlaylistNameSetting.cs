using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class SelectedPlaylistNameSetting : BaseSetting<string>
{
    public override string Key => "SelectedPlaylistName";
    public override string Title => "Selected Playlist Name";
    protected override string GetBaseDescription() => "Name of the last selected playlist.";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => string.Empty;
}
 