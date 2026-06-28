using Musicthingy.Settings;
using Musicthingy.Settings.Definitions;

namespace Musicthingy.Services;

public class SettingsManager
{
    // Make this static so BaseSetting can check it instantly.
    // It defaults to true so settings are locked the moment the app starts.
    public static bool IsRestoringState { get; set; } = true;

    public ThemeColorSetting ThemeSeedColor { get; }
    public ShuffleSetting ShuffleEnabled { get; }
    public RepeatModeSetting RepeatMode { get; }
    public SelectedPlaylistNameSetting SelectedPlaylistName { get; }
    public CurrentPositionSecondsSetting CurrentPositionSeconds { get; }
    public PlaybackQueueDataSetting PlaybackQueueData { get; }
    public CurrentTrackDataSetting CurrentTrackData { get; }
    public UseSystemThemeSetting UseSystemTheme { get; }

    private readonly IEnumerable<ISetting> _allSettings;

    public SettingsManager(IEnumerable<ISetting> settings)
    {
        _allSettings = settings;

        ThemeSeedColor = GetSetting<ThemeColorSetting>();
        ShuffleEnabled = GetSetting<ShuffleSetting>();
        RepeatMode = GetSetting<RepeatModeSetting>();
        SelectedPlaylistName = GetSetting<SelectedPlaylistNameSetting>();
        CurrentPositionSeconds = GetSetting<CurrentPositionSecondsSetting>();
        PlaybackQueueData = GetSetting<PlaybackQueueDataSetting>();
        CurrentTrackData = GetSetting<CurrentTrackDataSetting>();
        UseSystemTheme = GetSetting<UseSystemThemeSetting>();
    }

    private T GetSetting<T>() where T : class, ISetting
    {
        return _allSettings.OfType<T>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Setting {typeof(T).Name} not registered.");
    }

    public IEnumerable<ISetting> GetVisibleSettings() => _allSettings.Where(s => s.Category != SettingCategory.Hidden);

    public void LoadAll()
    {
        foreach (var setting in _allSettings) setting.Load();
    }
}