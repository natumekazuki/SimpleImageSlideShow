#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Xaml;

namespace SimpleImageSlideShow.Services
{
    internal class FolderPickerService : IFolderPicker
    {
        public async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            // Access the WinUI window from the current application instance
            var window = ((Microsoft.Maui.MauiWinUIApplication)Microsoft.UI.Xaml.Application.Current).MainWindow;
            var hwnd = WindowNative.GetWindowHandle(window);
            // Fix: Access the MainWindow property correctly using the Application.Current instance.
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainPage);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
#endif
