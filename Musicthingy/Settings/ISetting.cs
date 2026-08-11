using System.ComponentModel;
using System.Windows.Input;

namespace Musicthingy.Settings;

public enum SettingCategory { Appearance, Playback, General, Storage, Hidden }

public interface ISetting : INotifyPropertyChanged
{
    string Key { get; }
    string Title { get; }
    string Description { get; }
    string Icon { get; }
    SettingCategory Category { get; }

    // UI Metadata
    string TemplateName { get; }
    string DialogType { get; }

    // Unified UI Bindings
    string DisplayValue { get; set; }
    bool IsToggled { get; set; }
    IList<string>? Options { get; }

    void Load();
    void Save();
    void Reset();
}

public interface IActionSetting : ISetting
{
    ICommand ActionCommand { get; }
}