namespace Musicthingy.Settings.Definitions;

public class ShuffleSetting : BaseSetting<bool>
{
    public override string Key => "IsShuffleEnabled";
    public override string Title => "Shuffle";
    protected override string GetBaseDescription() => "Enable random playback order.";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override bool DefaultValue => false;
}