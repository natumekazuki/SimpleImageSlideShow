using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SimpleImageSlideShow.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            WindowHandler.Mapper.AppendToMapping(nameof(IWindow), (handler, view) =>
            {
                var window = handler.PlatformView.GetAppWindow();
                if (window != null)
                {
                    // resize & center to something we like
                    var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
                    const int width = 1920;
                    const int height = 1080;
                    window.MoveAndResize(new RectInt32((display.WorkArea.Width - width) / 2, (display.WorkArea.Height - height) / 2, width, height));

                    if (window.Presenter is OverlappedPresenter presenter)
                    {
                        //presenter.IsMinimizable = false;
                        presenter.IsMaximizable = false;
                    }
                }
            });
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
