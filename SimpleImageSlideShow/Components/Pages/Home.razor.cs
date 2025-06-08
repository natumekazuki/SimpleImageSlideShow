using Microsoft.AspNetCore.Components;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Home
    {
        [Inject]
        public required IImageService ImageService { get; init; }

        private List<IImageEntity> ImageEntities { get; set; } = [];

        private const uint delaySeconds = 3;

        private static async Task<string> SelectDirectoryAsync()
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

        private static readonly System.Timers.Timer timer = new(TimeSpan.FromSeconds(delaySeconds));

        private async Task LoadImagesAsync()
        {
            var directoryPath = await SelectDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            ImageService.LoadImages(directoryPath);
        }

        private async Task ReloadImageAsync()
        {
            var first = ImageEntities.Count > 3 ? ImageEntities[0] : null;
            if (first is not null)
            {
                ImageEntities.Remove(first);
            }
            var imagePath = ImageService.GetRandomImagePath();
            var imageEntity = await ImageService.LoadImageEntityAsync(imagePath);
            if (imageEntity is null) return;
            this.ImageEntities.Add(imageEntity);
        }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            await this.LoadImagesAsync();
            await this.ReloadImageAsync();

            timer.AutoReset = true;
            timer.Elapsed += async (_, __) =>
            {
                await InvokeAsync(async () =>
                {
                    await ReloadImageAsync();
                    this.StateHasChanged();
                });
            };
            timer.Start();
        }
    }
}
