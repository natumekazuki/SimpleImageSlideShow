using SimpleImageSlideShow.Models;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Home
    {
        private static readonly Random Rng = new();

        private string targetDirectoryPath = string.Empty;

        private readonly List<string> allImages = [];

        private async Task SelectDirectoryAsync()
        {
            var mauiWindow = Application.Current?.Windows[0].Handler.PlatformView as Microsoft.UI.Xaml.Window;
            if (mauiWindow == null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mauiWindow);
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folderPicked = await picker.PickSingleFolderAsync();
            if (folderPicked != null)
            {
                targetDirectoryPath = folderPicked.Path;
            }
        }

        private async Task LoadImagesAsync()
        {
            if (string.IsNullOrEmpty(targetDirectoryPath)) return;

            if (!Directory.Exists(targetDirectoryPath)) return;
        }
    }
}
