using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

public class UseSystemThemeSetting : BaseSetting<bool>
{
    private readonly ThemeManager _themeManager;
    public override string Key => "UseSystemTheme";
    public override string Title => "Use System Theme";
    public override string Icon => "paintbrush_vertical_icon.png";
    protected override string GetBaseDescription() => "Use the system theme for the theme color.";
    public override SettingCategory Category => SettingCategory.Appearance;
    public override bool DefaultValue => true;

    public UseSystemThemeSetting(ThemeManager themeManager)
    {
        _themeManager = themeManager;
    }

    protected override void OnValueChanged(bool useSystemTheme)
    {
        if(!useSystemTheme)
            return;

        Color? newColor = SystemThemeHelper.GetAndroidSystemAccentColor();

        if (newColor == null)
        {
            // If the system accent color is not available, we can set the toggle back to false.
            IsToggled = false;
            return;
        }
        _themeManager.ApplyTheme(newColor);
    }
}