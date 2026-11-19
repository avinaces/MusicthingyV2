using CommunityToolkit.Maui;
using MPowerKit.VirtualizeListView;
using Musicthingy.Search;
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
			// Use reflection-based automatic registration for app settings
			builder.Services.AddAppSettings();
			builder.Services.AddSingleton<ColorExtractionService>();

			// Search pipeline: providers + index + coordinator. Add a provider
			// (for example web search) in SearchServiceCollectionExtensions.
			builder.Services.AddSearch();

			// v9.3: the "youtube" named client sits on the resolver's cookie-
			// free InnerTubeHandler (UseCookies=false — Set-Cookie harvesting
			// from earlier /player answers must not leak into later anonymous
			// requests). Handler lifetime is infinite on purpose: the handler
			// is a static singleton shared with SABR and direct URL requests,
			// so letting IHttpClientFactory dispose/cycle it every 2 minutes
			// would tear down live SABR connections; PooledConnectionLifetime
			// already rotates connections underneath.
			builder.Services.AddHttpClient("youtube", c => {
				c.Timeout = TimeSpan.FromSeconds(30);
				c.DefaultRequestHeaders.UserAgent.ParseAdd(YouTubeResolverService.DefaultUserAgent);
			}).ConfigurePrimaryHttpMessageHandler(_ => YouTubeResolverService.InnerTubeHandler)
			  .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
			builder.Services.AddSingleton<YouTubeResolverService>(sp =>
				new YouTubeResolverService(sp.GetRequiredService<IHttpClientFactory>()));

			builder.Services.AddSingleton<Services.TrackCacheService>();


#if WINDOWS
			builder.Services.AddSingleton<LocalStreamProxy>();
			builder.Services.AddSingleton<Musicthingy.Services.IMediaProbingService, Musicthingy.Platforms.Windows.VlcMediaProbingService>();
#elif ANDROID
			builder.Services.AddSingleton<IMediaProbingService, Platforms.Android.ExoPlayerMediaProbingService>();
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
				.UseMauiCommunityToolkit(static options =>
				{
					options.SetPopupDefaults(new DefaultPopupSettings
					{
						Padding = 0,
					});

					options.SetPopupOptionsDefaults(new DefaultPopupOptionsSettings
					{
						Shape = null,
						CanBeDismissedByTappingOutsideOfPopup = true
					});
				})
				.UseMPowerKitListView()
				.ConfigureFonts(fonts => { });


			return builder.Build();
		}
	}
}


public static class SettingsExtensions
{
	public static IServiceCollection AddAppSettings(this IServiceCollection services)
	{
		// 1. Find all classes that inherit from ISetting
		var settingTypes = System.Reflection.Assembly.GetExecutingAssembly().GetTypes()
			.Where(t => !t.IsAbstract && !t.IsInterface && typeof(Musicthingy.Settings.ISetting).IsAssignableFrom(t));

		// 2. Register them automatically
		foreach (Type? type in settingTypes)
		{
			services.AddSingleton(typeof(Musicthingy.Settings.ISetting), type);
		}

		// 3. Register the manager
		services.AddSingleton<Musicthingy.Services.SettingsManager>();

		return services;
	}
}