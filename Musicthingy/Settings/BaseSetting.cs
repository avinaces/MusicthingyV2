using CommunityToolkit.Mvvm.ComponentModel;
using Musicthingy.Services;
using System.Text.Json;

namespace Musicthingy.Settings;

public abstract class BaseSetting<T> : ObservableObject, ISetting
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract SettingCategory Category { get; }
    public abstract T DefaultValue { get; }

    // Automatically resolve the UI type based on T
    public virtual string TemplateName => typeof(T) == typeof(bool) ? "SwitchTemplate" : "DialogTemplate";
    public virtual string DialogType => typeof(T).IsEnum ? "Picker" : typeof(T) == typeof(int) || typeof(T) == typeof(double) ? "Number" : "Text";
    public virtual string Icon => "settings_icon.png";

    public IList<string>? Options => typeof(T).IsEnum ? Enum.GetNames(typeof(T)).ToList() : null;

    // Dynamic description routing
    public virtual string Description => GetBaseDescription();
    protected abstract string GetBaseDescription();

    private T _value;
    public T Value
    {
        get => _value;
        set
        {
            if (SettingsManager.IsRestoringState) return;

            if (SetProperty(ref _value, value))
            {
                Save();
                OnValueChanged(value);

                // Refresh UI bindings instantly
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(IsToggled));
                OnPropertyChanged(nameof(Description));
            }
        }
    }

    public string DisplayValue
    {
        get => Value?.ToString() ?? string.Empty;
        set
        {
            if (SettingsManager.IsRestoringState) return;
            try
            {
                if (typeof(T) == typeof(string)) Value = (T)(object)value;
                else if (typeof(T).IsEnum) Value = (T)Enum.Parse(typeof(T), value);
                else Value = (T)Convert.ChangeType(value, typeof(T));
            }
            catch { /* Ignore invalid formats during parse */ }
        }
    }

    public bool IsToggled
    {
        get => Value is bool b && b;
        set { if (typeof(T) == typeof(bool)) Value = (T)(object)value; }
    }

    protected BaseSetting() { _value = DefaultValue; }

    protected virtual void OnValueChanged(T newValue) { }

    public void Load()
    {
        try
        {
            if (typeof(T) == typeof(bool)) _value = (T)(object)Preferences.Default.Get(Key, (bool)(object)DefaultValue!);
            else if (typeof(T) == typeof(int)) _value = (T)(object)Preferences.Default.Get(Key, (int)(object)DefaultValue!);
            else if (typeof(T) == typeof(double)) _value = (T)(object)Preferences.Default.Get(Key, (double)(object)DefaultValue!);
            else if (typeof(T) == typeof(string)) _value = (T)(object)Preferences.Default.Get(Key, (string)(object)DefaultValue!);
            else
            {
                var json = Preferences.Default.Get(Key, string.Empty);
                _value = !string.IsNullOrEmpty(json) ? (JsonSerializer.Deserialize<T>(json) ?? DefaultValue) : DefaultValue;
            }
        }
        catch { _value = DefaultValue; }

        // Push the loaded value to OnValueChanged even during state restoration.
        // IsRestoringState guards the Value SETTER (line 31) to prevent saves
        // during load, but Load() sets _value directly (bypassing the setter),
        // so OnValueChanged is never called. This means auth settings (cookies,
        // visitorData, etc.) are loaded from Preferences but never pushed to
        // YouTubeResolverService at startup. Fix: call OnValueChanged here.
        try { OnValueChanged(_value); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Settings] OnValueChanged error during Load for {Key}: {ex.Message}"); }

        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Value)));
    }

    public void Save()
    {
        try
        {
            if (_value is bool b) Preferences.Default.Set(Key, b);
            else if (_value is int i) Preferences.Default.Set(Key, i);
            else if (_value is double d) Preferences.Default.Set(Key, d);
            else if (_value is string s) Preferences.Default.Set(Key, s ?? string.Empty);
            else Preferences.Default.Set(Key, JsonSerializer.Serialize(_value));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Save failed: {ex}"); }
    }

    public void Reset() => Value = DefaultValue;
}