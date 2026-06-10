using CommunityToolkit.Maui;
using MPowerKit.VirtualizeListView;
using Musicthingy.Services;
using Musicthingy.ViewModels;

namespace Musicthingy
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
#if WINDOWS
            // Only initialize native VLC C++ libraries on Windows
            LibVLCSharp.Shared.Core.Initialize();
#endif

            var builder = MauiApp.CreateBuilder();

            // Register Services
            builder.Services.AddSingleton<ThemeManager>();
            builder.Services.AddSingleton<ColorExtractionService>();
            builder.Services.AddSingleton<YouTubeResolverService>();

            builder.Services.AddSingleton(sp => new HttpClient(YouTubeResolverService.Ipv4Handler));
            builder.Services.AddSingleton<LocalStreamProxy>();


#if WINDOWS
            builder.Services.AddSingleton<Musicthingy.Services.IMediaProbingService, Musicthingy.Platforms.Windows.VlcMediaProbingService>();
#elif ANDROID
            builder.Services.AddSingleton<Musicthingy.Services.IMediaProbingService, Musicthingy.Platforms.Android.ExoPlayerMediaProbingService>();
#endif

            // ----------------------------------------------------
            // PLATFORM SPECIFIC PLAYBACK ENGINE INJECTION
            // ----------------------------------------------------
#if ANDROID
            // Use native ExoPlayer on Android
            builder.Services.AddSingleton<IMediaPlaybackService, Platforms.Android.ExoPlayerMediaPlaybackService>();
#elif WINDOWS
            // Use LibVLC on Windows
            builder.Services.AddSingleton<IMediaPlaybackService, VlcMediaPlaybackService>();
#endif
            // ----------------------------------------------------

            builder.Services.AddSingleton<MainViewModel>();

            // Register your platform-specific views
            builder.Services.AddTransient<Views.MobileMainPage>();
            builder.Services.AddTransient<AppShell>();

            builder.UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .UseMPowerKitListView()
                .ConfigureFonts(fonts => { });


            return builder.Build();
        }
    }
}