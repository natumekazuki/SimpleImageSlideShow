using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow
{
    public static class MauiProgram
    {
        public static IServiceProvider Services { get; private set; } = default!;

        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                })
                .ConfigureLifecycleEvents(events =>
                {
#if WINDOWS
                    events.AddWindows(w =>
                    {
                        w.OnWindowCreated(window =>
                        {
                            const int width = 1920;
                            const int height = 1080;
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, width, height));
                        });
                    });
#endif
                });

            builder.Services.AddSingleton<Services.IFolderPicker, FolderPickerService>();
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            var app = builder.Build();
            Services = app.Services;
            return app;
        }
    }
}
