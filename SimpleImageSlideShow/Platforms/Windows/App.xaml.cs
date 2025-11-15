using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SimpleImageSlideShow.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        private static Mutex? _singleInstanceMutex;
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            try
            {
                bool createdNew;
                _singleInstanceMutex = new Mutex(true, "SimpleImageSlideShow_SingleInstance", out createdNew);
                if (!createdNew)
                {
                    Environment.Exit(0);
                    return;
                }
            }
            catch { }

            WindowHandler.Mapper.AppendToMapping(nameof(IWindow), (handler, view) =>
            {
                var appWindow = handler.PlatformView.GetAppWindow();
                var nativeWindow = handler.PlatformView as Microsoft.UI.Xaml.Window;
                nativeWindow.ExtendsContentIntoTitleBar = true;

                if (appWindow != null)
                {
                    try
                    {
                        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                        // Match title bar colors to app background (lightgray)
                        var tb = appWindow.TitleBar;
                        if (tb is not null)
                        {
                            var bg = Windows.UI.Color.FromArgb(255, 0xD3, 0xD3, 0xD3); // #D3D3D3 lightgray
                            var fg = Windows.UI.Color.FromArgb(255, 0xD3, 0xD3, 0xD3);
                            tb.BackgroundColor = bg;
                            tb.InactiveBackgroundColor = bg;
                            tb.ForegroundColor = fg;
                            tb.InactiveForegroundColor = fg;
                        }
                    }
                    catch { }
                }
            });
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
