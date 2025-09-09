using SimpleImageSlideShow.Models;
using System.Text.Json;

namespace SimpleImageSlideShow.Services
{
    public sealed class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private static string SettingsPath => Path.Combine(FileSystem.AppDataDirectory, nameof(SimpleImageSlideShow), "settings.json");

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    await using var fs = File.OpenRead(SettingsPath);
                    var settings = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOptions);
                    return settings ?? new AppSettings();
                }
            }
            catch
            {
                // ignore and fall back
            }

            return new AppSettings();
        }

        public async Task SaveAsync(AppSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await using var fs = File.Create(SettingsPath);
            await JsonSerializer.SerializeAsync(fs, settings, JsonOptions);
        }
    }
}

