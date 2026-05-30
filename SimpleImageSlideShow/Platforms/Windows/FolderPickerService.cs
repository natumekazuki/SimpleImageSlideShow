using SimpleImageSlideShow.Services;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Platforms.Windows
{
    internal sealed class FolderPickerService : IFolderPickerService
    {
        public async Task<string> SelectDirectoryAsync()
        {
            var mauiWindow = Application.Current?.Windows[0].Handler.PlatformView as Microsoft.UI.Xaml.Window;
            if (mauiWindow == null) return string.Empty;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mauiWindow);
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folderPicked = await picker.PickSingleFolderAsync();
            return folderPicked is not null ? folderPicked.Path : string.Empty;
        }
    }
}
