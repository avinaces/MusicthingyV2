using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using LibVLCSharp.Shared;

namespace Musicthingy
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {

            // Initialize the native VLC C++ libraries
            Core.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder.UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Roboto_VariableFont_wdth.ttf", "Roboto");
            })
                .UseMauiCommunityToolkitMediaElement(true);
#if DEBUG
            builder.Logging.AddDebug();
#endif
#if ANDROID
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("AllowAutoplay", (handler, view) =>
            {
                handler.PlatformView.Settings.MediaPlaybackRequiresUserGesture = false;
            });
#endif

            return builder.Build();
        }
    }
}