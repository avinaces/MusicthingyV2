using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

public class ThemeColorSetting : BaseSetting<string>
{
    private readonly ThemeManager _themeManager;

    public override string Key => "AppThemeSeedColor";
    public override string Title => "Theme Color";
    public override SettingCategory Category => SettingCategory.Appearance;
    public override string DefaultValue => "#005dff";

    // Request the specific M3 Dialog Type
    public override string DialogType => "ColorPicker";
    public override string Icon => "palette_icon.png";

    // Dynamic subtitle text
    protected override string GetBaseDescription() => $"Accent color for the application. Currently: {Value}";

    public ThemeColorSetting(ThemeManager themeManager)
    {
        _themeManager = themeManager;
    }

    protected override void OnValueChanged(string newValue)
    {
        _themeManager.ApplyTheme(Color.FromArgb(newValue));
    }
}