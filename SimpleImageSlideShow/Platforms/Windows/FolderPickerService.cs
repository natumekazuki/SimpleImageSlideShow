#if WINDOWS
using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow.Services
{
    internal class FolderPickerService : IFolderPicker
    {
        public async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker();
            var hwnd = WindowNative.GetWindowHandle(Microsoft.Maui.MauiWinUIApplication.Current.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
#endif
