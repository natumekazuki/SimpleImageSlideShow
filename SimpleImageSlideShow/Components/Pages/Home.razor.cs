using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SimpleImageSlideShow.Components.Pages.ImageLayoutViews;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Models.ImageLayout;
using SimpleImageSlideShow.Services;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Home
    {

        private const int Columns = 16;
        private const int TotalCells = Columns * Columns;

        [Inject]
        public required IImageService ImageService { get; init; }

        [Inject]
        public required IJSRuntime JS { get; init; }

        private List<IImageEntity> ImageEntities { get; set; } = [];


        private List<IImageEntity> NextImageEntities { get; set; } = [];

        private const uint delaySeconds = 5;

        private ImageLayoutEntity? ImageLayout { get; set; } = default;

        private readonly Random random = new();

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
            var imagePath = ImageService.GetRandomImagePath();
            var imageEntity = await ImageService.LoadImageEntityAsync(imagePath);
            if (imageEntity is null) return;
            this.ImageEntities.Add(imageEntity);
        }

        private async Task ReloadImageLayoutAsync()
        {
            foreach (var imageEntity in this.NextImageEntities)
            {
                this.ImageEntities.Remove(imageEntity);
            }

            NextImageEntities.Clear();

            var imageLayoutEntity = this.GetRandomImageLayoutEntity();

            int widthImageCount = (int)imageLayoutEntity.WideImageCount;
            int tallImageCount = (int)imageLayoutEntity.TallImageCount;

            int tryCount = 0;

            while ((int)imageLayoutEntity.WideImageCount > this.ImageEntities.Count(i => i.IsLandscape)
                   || (int)imageLayoutEntity.TallImageCount > this.ImageEntities.Count(i => !i.IsLandscape))
            {
                if(tryCount > 100)
                {
                    imageLayoutEntity = this.GetRandomImageLayoutEntity();
                    tryCount = 0;
                }

                await this.ReloadImageAsync();
                tryCount++;
            }

            var widthImages = this.ImageEntities.Where(i => i.IsLandscape).Take(widthImageCount).ToList();
            var tallImages = this.ImageEntities.Where(i => !i.IsLandscape).Take(tallImageCount).ToList();

            NextImageEntities.AddRange(widthImages);
            NextImageEntities.AddRange(tallImages);

            this.ImageLayout = imageLayoutEntity;
        }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            await this.LoadImagesAsync();
            await ReloadImageLayoutAsync();

            timer.AutoReset = true;
            timer.Elapsed += async (_, __) =>
            {
                await InvokeAsync(async () =>
                {
                    await ReloadImageLayoutAsync();
                    this.StateHasChanged();
                });
            };
            timer.Start();
        }

        private ImageLayoutEntity GetRandomImageLayoutEntity()
        {
            int index = random.Next(ImageLayouts.ImageLayoutEntities.Count);
            return ImageLayouts.ImageLayoutEntities[index];
        }
    }
}
