#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SimpleImageSlideShow.Services
{
    internal class FolderPickerService : IFolderPicker
    {
        public async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            // Fix: Access the MainWindow property correctly using the Application.Current instance.
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainPage);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
#endif
