using SimpleImageSlideShow.Platforms.Windows;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
#if WINDOWS
            var userDataFolder = Path.Combine(FileSystem.AppDataDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
#endif
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
            builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();
#endif

            return builder.Build();
        }
    }
}
