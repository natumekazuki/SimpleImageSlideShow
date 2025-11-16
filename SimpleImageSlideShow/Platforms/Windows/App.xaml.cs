using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using SimpleImageSlideShow.Services;
using System.Globalization;
using System.Threading.Tasks;

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
                        // Match title bar colors to user-selected background
                        var tb = appWindow.TitleBar;
                        if (tb is not null)
                        {
                            ApplyTitleBarColor(tb, DefaultTitleBarColor);
                            _ = ResolveAndApplyTitleBarColorAsync(nativeWindow, tb);
                        }
                    }
                    catch { }
                }
            });
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        private static async Task ResolveAndApplyTitleBarColorAsync(Microsoft.UI.Xaml.Window window, AppWindowTitleBar titleBar)
        {
            try
            {
                var settingsService = new SettingsService();
                var settings = await settingsService.LoadAsync().ConfigureAwait(false);
                var color = ParseColorOrDefault(settings?.BackgroundColor);

                _ = window.DispatcherQueue.TryEnqueue(() => ApplyTitleBarColor(titleBar, color));
            }
            catch
            {
                // ignore failures and keep default colors
            }
        }

        private static void ApplyTitleBarColor(AppWindowTitleBar tb, Windows.UI.Color color)
        {
            var fg = color;
            tb.BackgroundColor = color;
            tb.InactiveBackgroundColor = color;
            tb.ForegroundColor = fg;
            tb.InactiveForegroundColor = fg;
            tb.ButtonBackgroundColor = color;
            tb.ButtonInactiveBackgroundColor = color;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = fg;
        }

        private static Windows.UI.Color ParseColorOrDefault(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return DefaultTitleBarColor;
            var trimmed = hex.Trim();
            if (trimmed.StartsWith('#')) trimmed = trimmed[1..];

            if (uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                if (trimmed.Length == 6)
                {
                    var r = (byte)((value & 0xFF0000) >> 16);
                    var g = (byte)((value & 0x00FF00) >> 8);
                    var b = (byte)(value & 0x0000FF);
                    return Windows.UI.Color.FromArgb(255, r, g, b);
                }
                if (trimmed.Length == 8)
                {
                    var a = (byte)((value & 0xFF000000) >> 24);
                    var r = (byte)((value & 0x00FF0000) >> 16);
                    var g = (byte)((value & 0x0000FF00) >> 8);
                    var b = (byte)(value & 0x000000FF);
                    return Windows.UI.Color.FromArgb(a, r, g, b);
                }
            }

            return DefaultTitleBarColor;
        }

        private static Windows.UI.Color DefaultTitleBarColor { get; } = Windows.UI.Color.FromArgb(255, 0xD3, 0xD3, 0xD3);
    }

}
