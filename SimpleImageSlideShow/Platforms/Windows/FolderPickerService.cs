#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SimpleImageSlideShow.Services
{
    internal class FolderPickerService : IFolderPicker
    {
        public async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            // Access the WinUI window from the current application instance
            var window = Application.Current?.Windows[0].Handler.PlatformView as Microsoft.UI.Xaml.Window;
            if (window is null) return null;
            nint hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
#endif
