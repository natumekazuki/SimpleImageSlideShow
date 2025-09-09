using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using System.Threading;

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
                if (appWindow != null)
                {
                    try
                    {
                        // True full screen (no title bar, covers taskbar)
                        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                        // Match title bar colors to app background (lightgray)
                        var tb = appWindow.TitleBar;
                        if (tb is not null)
                        {
                            var bg = Windows.UI.Color.FromArgb(255, 0xD3, 0xD3, 0xD3); // #D3D3D3 lightgray
                            var fg = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                            tb.BackgroundColor = bg;
                            tb.InactiveBackgroundColor = bg;
                            tb.ButtonBackgroundColor = bg;
                            tb.ButtonInactiveBackgroundColor = bg;
                            tb.ButtonHoverBackgroundColor = bg;
                            tb.ButtonPressedBackgroundColor = bg;
                            tb.ForegroundColor = fg;
                            tb.InactiveForegroundColor = fg;
                            tb.ButtonForegroundColor = fg;
                        }
                    }
                    catch { }

                    if (nativeWindow is not null)
                    {
                        // Ensure it stays full screen when window activates
                        nativeWindow.Activated += (_, __) =>
                        {
                            try
                            {
                                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                                var tb = appWindow.TitleBar;
                                if (tb is not null)
                                {
                                    var bg = Windows.UI.Color.FromArgb(255, 0xD3, 0xD3, 0xD3);
                                    var fg = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                                    tb.BackgroundColor = bg;
                                    tb.InactiveBackgroundColor = bg;
                                    tb.ButtonBackgroundColor = bg;
                                    tb.ButtonInactiveBackgroundColor = bg;
                                    tb.ButtonHoverBackgroundColor = bg;
                                    tb.ButtonPressedBackgroundColor = bg;
                                    tb.ForegroundColor = fg;
                                    tb.InactiveForegroundColor = fg;
                                    tb.ButtonForegroundColor = fg;
                                }
                            }
                            catch { }
                        };
                    }
                }
            });
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
