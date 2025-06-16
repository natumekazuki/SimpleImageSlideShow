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

        private List<IImageEntity> ImageEntities_1 { get; set; } = [];
        private List<IImageEntity> ImageEntities_2 { get; set; } = [];

        private uint DelaySeconds { get; set; } = DefaultDelaySeconds;

        private readonly uint A = DefaultDelaySeconds * 2;

        private const uint DefaultDelaySeconds = 2;

        private int Count = 0;

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

            this.ImageEntities_1.Clear();
            await this.ReloadImageAsync();

            this.Timer = new(TimeSpan.FromSeconds(DelaySeconds));
            Timer.AutoReset = true;
            Timer.Elapsed += async (_, __) =>
            {
                await InvokeAsync(async () =>
                {
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

        private bool useFirstList = true;

        private async Task ReloadImageAsync()
        {
            var imagePath = ImageService.GetRandomImagePath();
            var imageEntity = await ImageService.LoadImageEntityAsync(imagePath);
            if (imageEntity is null) return;
            Count++;

            if (useFirstList)
            {
                ImageEntities_1.Add(imageEntity);

                if (ImageEntities_1.Count == 9)
                {
                    // 次でImageEntities_1に戻る直前にImageEntities_1をクリア
                    ImageEntities_2.Clear();
                }

                if (ImageEntities_1.Count % 10 == 0)
                {
                    useFirstList = false;
                }
            }
            else
            {
                ImageEntities_2.Add(imageEntity);
                if (ImageEntities_2.Count == 9)
                {
                    // 次でImageEntities_1に戻る直前にImageEntities_1をクリア
                    ImageEntities_1.Clear();
                }
                if (ImageEntities_2.Count % 10 == 0)
                {
                    useFirstList = true;
                }
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
                    await this.ReloadImageAsync();
                    this.StateHasChanged();
                });
            };
            Timer.Start();
        }
    }
}
