using SimpleImageSlideShow.Platforms.Windows;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            builder.Services.AddSingleton<FrameService>();
            builder.Services.AddSingleton<ISettingsService, SettingsService>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
    		//builder.Logging.AddDebug();
            
#endif

#if WINDOWS
            builder.Services.AddSingleton<IImageService, ImageService>();
            builder.Services.AddSingleton<IWindowService, WindowService>();
            builder.Services.AddSingleton<IWebViewHostService, WebViewHostService>();
#endif

            return builder.Build();
        }
    }
}
