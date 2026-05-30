using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public interface ISettingsService
    {
        Task<AppSettings> LoadAsync();
        Task<SettingsProfile> LoadActiveProfileAsync();
        Task<IReadOnlyList<SettingsProfileSummary>> ListProfilesAsync();
        Task SaveAsync(AppSettings settings);
        Task<string> CreateProfileAsync(string name, AppSettings settings, bool isActive);
        Task RenameProfileAsync(string profileId, string name);
        Task SetActiveProfileAsync(string profileId);
        Task<bool> DeleteProfileAsync(string profileId);
    }
}

