using Musicthingy.Services;

namespace Musicthingy.Settings.Definitions;

/// <summary>
/// YouTube visitorData token (Cg... format).
/// Format matches OpenTune's "***VISITOR DATA***" field exactly.
///
/// Optional — if not set, the resolver populates it automatically from the
/// first successful player response. Setting it manually can help with the
/// first-track-load experience (avoids one round-trip).
///
/// Example value:
///   CgTqZlN2ckRyUVdaWSiUlpzUBjIoCgJMVhIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4f...
/// </summary>
public class VisitorDataSetting : BaseSetting<string>
{
    public override string Key => "VisitorData";
    public override string Title => "Visitor Data";
    public override SettingCategory Category => SettingCategory.Hidden;
    public override string DefaultValue => "";
    public override string DialogType => "Text";
    public override string Icon => "key_icon.png";

    protected override string GetBaseDescription()
    {
        if (string.IsNullOrWhiteSpace(Value))
            return "Not set. Will auto-populate from YouTube responses.";
        return "visitorData token (Cg... format). Sent with every InnerTube request.";
    }

    protected override void OnValueChanged(string newValue)
    {
        YouTubeResolverService.VisitorData = newValue;
    }
}
