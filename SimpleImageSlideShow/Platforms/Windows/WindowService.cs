using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;

namespace SimpleImageSlideShow.Platforms.Windows
{
    internal sealed class WindowService : Services.IWindowService
    {
        public void ToggleFullScreen()
        {
            try
            {
                var mauiWindow = Application.Current?.Windows.FirstOrDefault();
                if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                    return;

                var appWindow = nativeWindow.GetAppWindow();
                if (appWindow is null) return;

                if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                }
                else
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Exit()
        {
            try
            {
                var mauiWindow = Application.Current?.Windows.FirstOrDefault();
                if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    nativeWindow.Close();
                    return;
                }
            }
            catch { }

            try { Application.Current?.Quit(); } catch { }
            try { Environment.Exit(0); } catch { }
        }
    }
}
