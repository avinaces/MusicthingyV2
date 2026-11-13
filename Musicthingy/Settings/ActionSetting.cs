using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;

namespace Musicthingy.Settings;

public abstract class ActionSetting : ObservableObject, IActionSetting
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract SettingCategory Category { get; }

    public string TemplateName => "ActionTemplate";
    public string DialogType => string.Empty;

    // Default icon: circle_play_icon.png (the app's existing lucide "circle-play"
    // asset, also used for the Playback category). The previous default
    // "action_icon.png" does not exist in Resources/Images/lucide — every
    // action row without an explicit Icon override rendered a blank icon
    // (Glide FileNotFoundException in logcat). Derivables override with their
    // own existing icon; see ImportYouTubeAuthSetting / ClearYouTubeAuthSetting.
    public virtual string Icon => "circle_play_icon.png";

    public virtual string Description => GetBaseDescription();
    protected abstract string GetBaseDescription();

    public string DisplayValue { get; set; } = string.Empty;
    public bool IsToggled { get; set; }
    public IList<string>? Options => null;

    public ICommand ActionCommand { get; }

    protected ActionSetting()
    {
        ActionCommand = new RelayCommand(ExecuteAction);
    }

    public abstract void ExecuteAction();
    public void Load() { }
    public void Save() { }
    public void Reset() { }
}
