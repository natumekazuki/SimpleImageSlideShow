using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public interface IImageService
    {


        IEnumerable<string> LoadImages(string directoryPath);

        string GetRandomImagePath();

        Task<IImageEntity?> LoadImageEntityAsync(string imagePath);

        // Lightweight helper for scenarios (like Tiled mode) that only need size.
        // Returns (Width, Height) in pixels or null on failure.
        Task<(double Width, double Height)?> GetImageSizeAsync(string imagePath);

        void Dispose();
    }
}
