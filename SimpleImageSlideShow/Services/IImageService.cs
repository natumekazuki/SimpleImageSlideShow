using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public interface IImageService
    {


        IEnumerable<string> LoadImages(string directoryPath);

        string GetRandomImagePath();

        Task<IImageEntity?> LoadImageEntityAsync(string imagePath);

        void Dispose();
    }
}
