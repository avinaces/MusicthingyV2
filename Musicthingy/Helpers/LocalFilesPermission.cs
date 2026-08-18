using static Microsoft.Maui.ApplicationModel.Permissions;

namespace Musicthingy.Helpers;

public class LocalFilesPermission : BasePlatformPermission
{
#if ANDROID
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                return new[] { ("android.permission.READ_MEDIA_AUDIO", true) };
            }
            return new[] { ("android.permission.READ_EXTERNAL_STORAGE", true) };
        }
    }
#endif
}