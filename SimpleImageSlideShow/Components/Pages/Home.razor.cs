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

        private uint DelaySeconds { get; set; } = DefaultDelaySeconds;

        private const uint DefaultDelaySeconds = 5;

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

        private System.Timers.Timer Timer = new(TimeSpan.FromSeconds(DefaultDelaySeconds));

        private async Task UpdateTimerAsync()
        {
            this.Timer.Stop();

            this.ImageEntities.Clear();
            await this.ReloadImageAsync();

            this.Timer = new(TimeSpan.FromSeconds(DelaySeconds));
            Timer.AutoReset = true;
            Timer.Elapsed += async (_, __) =>
            {
                await InvokeAsync(async () =>
                {
                    this.RemoveFirstImage();
                    await this.ReloadImageAsync();
                    this.StateHasChanged();
                });
            };
            Timer.Start();
        }

        private async Task LoadImagesAsync()
        {
            var directoryPath = await SelectDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            ImageService.LoadImages(directoryPath);
        }

        private async Task ReloadImageAsync()
        {
            var imagePath = ImageService.GetRandomImagePath();
            var imageEntity = await ImageService.LoadImageEntityAsync(imagePath);
            if (imageEntity is null) return;
            this.ImageEntities.Add(imageEntity);
        }

        private void RemoveFirstImage()
        {
            if(this.ImageEntities.Count > 5)
            {
                this.ImageEntities.RemoveAt(0);
            }
        }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            await this.LoadImagesAsync();
            await this.ReloadImageAsync();

            Timer.AutoReset = true;
            Timer.Elapsed += async (_, __) =>
            {
                await InvokeAsync(async () =>
                {
                    this.RemoveFirstImage();
                    await this.ReloadImageAsync();
                    this.StateHasChanged();
                });
            };
            Timer.Start();
        }
    }
}
