using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class MaxCacheSizeSetting : BaseSetting<int>
{
    public override string Key => "MaxCacheSizeMB";
    public override string Title => "Max Cache Size (MB)";
    public override SettingCategory Category => SettingCategory.Storage;
    public override int DefaultValue => 1024; // 1 GB default
    public override string DialogType => "Number";
    public override string Icon => "hard_drive_icon.png";

    protected override string GetBaseDescription()
    {
        if (Value == 0)
            return "Unlimited. The cache will grow without restriction.";
        return $"Limit: {Value} MB. Set to 0 for unlimited. Oldest tracks are deleted when the limit is reached.";
    }

    protected override void OnValueChanged(int newValue)
    {
        // 0 = unlimited (no enforcement). Any positive value = enforce.
        if (newValue > 0)
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext?.Services.GetService<Services.TrackCacheService>() is { } cache)
            {
                Task.Run(() => cache.EnforceCacheLimit());
            }
        }
    }
}