using Android.App;
using Android.Runtime;

namespace Musicthingy
{
    [Application(AppCategory = Android.Content.PM.ApplicationCategories.Audio)]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
