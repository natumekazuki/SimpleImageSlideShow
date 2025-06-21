using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow.Platforms.Windows
{
    internal class ImageService(FrameService frameService) : IImageService
    {
        private static readonly Random Rng = new();


        private List<string> AllImages { get; init; } = [];

        private FileSystemWatcher? watcher;
        private readonly object _lock = new();


        string IImageService.GetRandomImagePath()
        {
            lock (_lock)
            {
                if (AllImages.Count == 0) return string.Empty;
                int idx = Rng.Next(this.AllImages.Count);
                return this.AllImages[idx];
            }
        }

        async Task<IImageEntity?> IImageService.LoadImageEntityAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return await Task.FromResult<ImageEntity?>(null);
            }

            using var img = System.Drawing.Image.FromFile(imagePath);
            double width = img.Width;
            double height = img.Height;
            img.Dispose();

            byte[] bytes = await File.ReadAllBytesAsync(imagePath);

            return new ImageEntity()
            {
                FilePath = imagePath,
                BytesImage = bytes,
                Width = width,
                Height = height,
                CssClass = frameService.GetFrameCss()
            };
        }

        IEnumerable<string> IImageService.LoadImages(string directoryPath)
        {
            if(string.IsNullOrWhiteSpace(directoryPath)) return this.AllImages;
            this.LoadImages(directoryPath);
            return this.AllImages;
        }

        private void LoadImages(string directoryPath)
        {
            AllImages.Clear();

            if (!Directory.Exists(directoryPath)) return;

            var files = Directory
                .EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(Models.ImageExtensions.IsImageFile);

            AllImages.AddRange(files);

            watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.EnableRaisingEvents = true;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            var ext = Path.GetExtension(e.FullPath);
            if (!Models.ImageExtensions.IsImageFile(ext)) return;

            lock (_lock)
            {
                if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    AllImages.Add(e.FullPath);
                }
                else if (e.ChangeType == WatcherChangeTypes.Deleted)
                {
                    AllImages.RemoveAll(p => p.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            var oldExt = Path.GetExtension(e.OldFullPath);
            var newExt = Path.GetExtension(e.FullPath);

            lock (_lock)
            {
                if (Models.ImageExtensions.IsImageFile(oldExt))
                {
                    AllImages.RemoveAll(p => p.Equals(e.OldFullPath, StringComparison.OrdinalIgnoreCase));
                }

                if (!Models.ImageExtensions.IsImageFile(newExt))
                {
                    AllImages.Add(e.FullPath);
                }
            }
        }

        void IImageService.Dispose()
        {
            watcher?.Dispose();
        }
    }
}
