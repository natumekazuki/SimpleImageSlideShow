using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public interface ISettingsService
    {
        Task<AppSettings> LoadAsync();
        Task SaveAsync(AppSettings settings);
    }
}

