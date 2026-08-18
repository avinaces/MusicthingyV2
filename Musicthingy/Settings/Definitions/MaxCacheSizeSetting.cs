using Musicthingy.Settings;

namespace Musicthingy.Settings.Definitions;

public class MaxCacheSizeSetting : BaseSetting<int>
{
    public override string Key => "MaxCacheSizeMB";
    public override string Title => "Max Cache Size (MB)";
    public override SettingCategory Category => SettingCategory.Storage;
    public override int DefaultValue => 1024; // 1 GB default
    public override string DialogType => "Number";
    public override string Icon => "hard_drive_icon.png"; // Assuming you have an icon like this

    protected override string GetBaseDescription() => $"Limit: {Value} MB. The oldest tracks will be deleted automatically when the limit is reached.";

    protected override void OnValueChanged(int newValue)
    {
        base.OnValueChanged(newValue);

        // Trigger a cleanup dynamically if the user lowers the limit below current usage
        if (Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext?.Services.GetService<Services.TrackCacheService>() is { } cache)
        {
            Task.Run(() => cache.EnforceCacheLimit());
        }
    }
}