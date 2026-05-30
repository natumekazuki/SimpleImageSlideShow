using Microsoft.Data.Sqlite;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;
using Xunit;

namespace SimpleImageSlideShow.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string settingsDirectory;

    public SettingsServiceTests()
    {
        settingsDirectory = Path.Combine(Path.GetTempPath(), "SimpleImageSlideShow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
    }

    [Fact]
    public async Task LoadAsync_CreatesDefaultActiveProfileWithoutJsonMigration()
    {
        await File.WriteAllTextAsync(Path.Combine(settingsDirectory, "settings.json"), """
            {
              "DirectoryPath": "C:\\legacy-images",
              "DelaySeconds": 42,
              "BackgroundColor": "#000000"
            }
            """);
        var service = new SettingsService(settingsDirectory);

        var settings = await service.LoadAsync();

        Assert.Null(settings.DirectoryPath);
        Assert.Equal(5u, settings.MinDelaySeconds);
        Assert.Equal(5u, settings.MaxDelaySeconds);
        Assert.Equal("#D3D3D3", settings.BackgroundColor);

        await using var connection = await OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, is_active, COUNT(*) OVER () FROM settings_profiles LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Default", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }

    [Fact]
    public async Task SaveAsync_PersistsSettingsIntoActiveProfile()
    {
        var service = new SettingsService(settingsDirectory);
        var expectedDirectory = Path.Combine(settingsDirectory, "images");

        await service.SaveAsync(new AppSettings
        {
            MinDelaySeconds = 0,
            MaxDelaySeconds = 3,
            DirectoryPath = expectedDirectory,
            WindowDisplayMode = "Windowed",
            TiledMinScale = 0.25,
            TiledMaxScale = 0.75,
            TiledCols = 8,
            MinTilePx = 96,
            TiledReuseTtlSeconds = 30,
            RandomScaleTries = 12,
            ShowTiledClock = false,
            TiledClockCorner = "TopRight",
            TiledClockScale = 1.5,
            AvoidTiledClockOverlap = false,
            BackgroundColor = "#112233"
        });

        var loaded = await service.LoadAsync();

        Assert.Equal(0u, loaded.MinDelaySeconds);
        Assert.Equal(3u, loaded.MaxDelaySeconds);
        Assert.Equal(expectedDirectory, loaded.DirectoryPath);
        Assert.Equal("Windowed", loaded.WindowDisplayMode);
        Assert.Equal(0.25, loaded.TiledMinScale);
        Assert.Equal(0.75, loaded.TiledMaxScale);
        Assert.Equal(8, loaded.TiledCols);
        Assert.Equal(96, loaded.MinTilePx);
        Assert.Equal(30, loaded.TiledReuseTtlSeconds);
        Assert.Equal(12u, loaded.RandomScaleTries);
        Assert.False(loaded.ShowTiledClock);
        Assert.Equal("TopRight", loaded.TiledClockCorner);
        Assert.Equal(1.5, loaded.TiledClockScale);
        Assert.False(loaded.AvoidTiledClockOverlap);
        Assert.Equal("#112233", loaded.BackgroundColor);
    }

    [Fact]
    public async Task SaveAsync_NormalizesInvalidDelayBounds()
    {
        var service = new SettingsService(settingsDirectory);

        await service.SaveAsync(new AppSettings
        {
            MinDelaySeconds = 12,
            MaxDelaySeconds = 0
        });

        var loaded = await service.LoadAsync();

        Assert.Equal(12u, loaded.MinDelaySeconds);
        Assert.Equal(12u, loaded.MaxDelaySeconds);
    }

    [Fact]
    public async Task SaveAsync_CreatesExpectedSchemaVersion()
    {
        var service = new SettingsService(settingsDirectory);

        await service.LoadAsync();

        await using var connection = await OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        var userVersion = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, userVersion);
    }

    private async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(settingsDirectory, "settings.db")}");
        await connection.OpenAsync();
        return connection;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(settingsDirectory))
            {
                Directory.Delete(settingsDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
