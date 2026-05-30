using Microsoft.Data.Sqlite;
using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public sealed class SettingsService : ISettingsService
    {
        private const int SchemaVersion = 1;
        private const string DefaultProfileName = "Default";
        private static readonly SemaphoreSlim DatabaseLock = new(1, 1);
        private readonly string _settingsDirectory;

        public SettingsService()
            : this(Path.Combine(FileSystem.AppDataDirectory, nameof(SimpleImageSlideShow)))
        {
        }

        internal SettingsService(string settingsDirectory)
        {
            _settingsDirectory = settingsDirectory;
        }

        private string DatabasePath => Path.Combine(_settingsDirectory, "settings.db");

        public async Task<AppSettings> LoadAsync()
        {
            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);
                return await LoadActiveProfileAsync(connection) ?? new AppSettings();
            }
            catch
            {
                // ignore and fall back
                return new AppSettings();
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                var profileId = await GetActiveProfileIdAsync(connection) ?? Guid.NewGuid().ToString("D");
                await UpsertProfileAsync(connection, profileId, DefaultProfileName, isActive: true, Normalize(settings));
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        private async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            return connection;
        }

        private static async Task EnsureDatabaseAsync(SqliteConnection connection)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS settings_profiles (
                        id TEXT PRIMARY KEY NOT NULL,
                        name TEXT NOT NULL,
                        is_active INTEGER NOT NULL DEFAULT 0,
                        directory_path TEXT NULL,
                        min_delay_seconds INTEGER NOT NULL,
                        max_delay_seconds INTEGER NOT NULL,
                        window_display_mode TEXT NOT NULL,
                        tiled_min_scale REAL NOT NULL,
                        tiled_max_scale REAL NOT NULL,
                        tiled_cols INTEGER NOT NULL,
                        min_tile_px INTEGER NOT NULL,
                        tiled_reuse_ttl_seconds INTEGER NOT NULL,
                        random_scale_tries INTEGER NOT NULL,
                        show_tiled_clock INTEGER NOT NULL,
                        tiled_clock_corner TEXT NOT NULL,
                        tiled_clock_scale REAL NOT NULL,
                        avoid_tiled_clock_overlap INTEGER NOT NULL,
                        background_color TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_settings_profiles_active
                        ON settings_profiles(is_active)
                        WHERE is_active = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                var result = await command.ExecuteScalarAsync();
                if (Convert.ToInt32(result) < SchemaVersion)
                {
                    command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private static async Task EnsureInitialProfileAsync(SqliteConnection connection)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM settings_profiles;";
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            if (count > 0) return;

            await UpsertProfileAsync(connection, Guid.NewGuid().ToString("D"), DefaultProfileName, isActive: true, Normalize(new AppSettings()));
        }

        private static async Task<string?> GetActiveProfileIdAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM settings_profiles WHERE is_active = 1 LIMIT 1;";
            return await command.ExecuteScalarAsync() as string;
        }

        private static async Task<AppSettings?> LoadActiveProfileAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    directory_path,
                    min_delay_seconds,
                    max_delay_seconds,
                    window_display_mode,
                    tiled_min_scale,
                    tiled_max_scale,
                    tiled_cols,
                    min_tile_px,
                    tiled_reuse_ttl_seconds,
                    random_scale_tries,
                    show_tiled_clock,
                    tiled_clock_corner,
                    tiled_clock_scale,
                    avoid_tiled_clock_overlap,
                    background_color
                FROM settings_profiles
                WHERE is_active = 1
                LIMIT 1;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return Normalize(new AppSettings
            {
                DirectoryPath = reader.IsDBNull(0) ? null : reader.GetString(0),
                MinDelaySeconds = ToUInt32(reader.GetInt64(1), 5),
                MaxDelaySeconds = ToUInt32(reader.GetInt64(2), 5),
                WindowDisplayMode = reader.GetString(3),
                TiledMinScale = reader.GetDouble(4),
                TiledMaxScale = reader.GetDouble(5),
                TiledCols = ToInt32(reader.GetInt64(6), 6),
                MinTilePx = ToInt32(reader.GetInt64(7), 128),
                TiledReuseTtlSeconds = ToInt32(reader.GetInt64(8), 120),
                RandomScaleTries = ToUInt32(reader.GetInt64(9), 10),
                ShowTiledClock = reader.GetInt64(10) != 0,
                TiledClockCorner = reader.GetString(11),
                TiledClockScale = reader.GetDouble(12),
                AvoidTiledClockOverlap = reader.GetInt64(13) != 0,
                BackgroundColor = reader.GetString(14)
            });
        }

        private static async Task UpsertProfileAsync(SqliteConnection connection, string profileId, string name, bool isActive, AppSettings settings)
        {
            if (isActive)
            {
                await using var deactivateCommand = connection.CreateCommand();
                deactivateCommand.CommandText = "UPDATE settings_profiles SET is_active = 0 WHERE id <> $id;";
                deactivateCommand.Parameters.AddWithValue("$id", profileId);
                await deactivateCommand.ExecuteNonQueryAsync();
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings_profiles (
                    id,
                    name,
                    is_active,
                    directory_path,
                    min_delay_seconds,
                    max_delay_seconds,
                    window_display_mode,
                    tiled_min_scale,
                    tiled_max_scale,
                    tiled_cols,
                    min_tile_px,
                    tiled_reuse_ttl_seconds,
                    random_scale_tries,
                    show_tiled_clock,
                    tiled_clock_corner,
                    tiled_clock_scale,
                    avoid_tiled_clock_overlap,
                    background_color,
                    created_at,
                    updated_at
                )
                VALUES (
                    $id,
                    $name,
                    $is_active,
                    $directory_path,
                    $min_delay_seconds,
                    $max_delay_seconds,
                    $window_display_mode,
                    $tiled_min_scale,
                    $tiled_max_scale,
                    $tiled_cols,
                    $min_tile_px,
                    $tiled_reuse_ttl_seconds,
                    $random_scale_tries,
                    $show_tiled_clock,
                    $tiled_clock_corner,
                    $tiled_clock_scale,
                    $avoid_tiled_clock_overlap,
                    $background_color,
                    $created_at,
                    $updated_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    is_active = excluded.is_active,
                    directory_path = excluded.directory_path,
                    min_delay_seconds = excluded.min_delay_seconds,
                    max_delay_seconds = excluded.max_delay_seconds,
                    window_display_mode = excluded.window_display_mode,
                    tiled_min_scale = excluded.tiled_min_scale,
                    tiled_max_scale = excluded.tiled_max_scale,
                    tiled_cols = excluded.tiled_cols,
                    min_tile_px = excluded.min_tile_px,
                    tiled_reuse_ttl_seconds = excluded.tiled_reuse_ttl_seconds,
                    random_scale_tries = excluded.random_scale_tries,
                    show_tiled_clock = excluded.show_tiled_clock,
                    tiled_clock_corner = excluded.tiled_clock_corner,
                    tiled_clock_scale = excluded.tiled_clock_scale,
                    avoid_tiled_clock_overlap = excluded.avoid_tiled_clock_overlap,
                    background_color = excluded.background_color,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", profileId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
            command.Parameters.AddWithValue("$directory_path", (object?)settings.DirectoryPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$min_delay_seconds", settings.MinDelaySeconds);
            command.Parameters.AddWithValue("$max_delay_seconds", settings.MaxDelaySeconds);
            command.Parameters.AddWithValue("$window_display_mode", settings.WindowDisplayMode);
            command.Parameters.AddWithValue("$tiled_min_scale", settings.TiledMinScale);
            command.Parameters.AddWithValue("$tiled_max_scale", settings.TiledMaxScale);
            command.Parameters.AddWithValue("$tiled_cols", settings.TiledCols);
            command.Parameters.AddWithValue("$min_tile_px", settings.MinTilePx);
            command.Parameters.AddWithValue("$tiled_reuse_ttl_seconds", settings.TiledReuseTtlSeconds);
            command.Parameters.AddWithValue("$random_scale_tries", settings.RandomScaleTries);
            command.Parameters.AddWithValue("$show_tiled_clock", settings.ShowTiledClock ? 1 : 0);
            command.Parameters.AddWithValue("$tiled_clock_corner", settings.TiledClockCorner);
            command.Parameters.AddWithValue("$tiled_clock_scale", settings.TiledClockScale);
            command.Parameters.AddWithValue("$avoid_tiled_clock_overlap", settings.AvoidTiledClockOverlap ? 1 : 0);
            command.Parameters.AddWithValue("$background_color", settings.BackgroundColor);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            await command.ExecuteNonQueryAsync();
        }

        private static AppSettings Normalize(AppSettings settings)
        {
            var delayRange = DelayRange.Normalize(settings.MinDelaySeconds, settings.MaxDelaySeconds);

            return new AppSettings
            {
                MinDelaySeconds = delayRange.MinSeconds,
                MaxDelaySeconds = delayRange.MaxSeconds,
                DirectoryPath = settings.DirectoryPath,
                WindowDisplayMode = string.IsNullOrWhiteSpace(settings.WindowDisplayMode) ? "FullScreen" : settings.WindowDisplayMode,
                TiledMinScale = settings.TiledMinScale,
                TiledMaxScale = settings.TiledMaxScale,
                TiledCols = settings.TiledCols > 0 ? settings.TiledCols : 6,
                MinTilePx = settings.MinTilePx > 0 ? settings.MinTilePx : 128,
                TiledReuseTtlSeconds = settings.TiledReuseTtlSeconds > 0 ? settings.TiledReuseTtlSeconds : 120,
                ShowTiledClock = settings.ShowTiledClock,
                TiledClockCorner = string.IsNullOrWhiteSpace(settings.TiledClockCorner) ? "BottomLeft" : settings.TiledClockCorner,
                TiledClockScale = settings.TiledClockScale > 0 ? settings.TiledClockScale : 1.0,
                AvoidTiledClockOverlap = settings.AvoidTiledClockOverlap,
                RandomScaleTries = settings.RandomScaleTries > 0 ? settings.RandomScaleTries : 10,
                BackgroundColor = string.IsNullOrWhiteSpace(settings.BackgroundColor) ? "#D3D3D3" : settings.BackgroundColor
            };
        }

        private static int ToInt32(long value, int fallback)
        {
            if (value < int.MinValue || value > int.MaxValue) return fallback;
            return (int)value;
        }

        private static uint ToUInt32(long value, uint fallback)
        {
            if (value < uint.MinValue || value > uint.MaxValue) return fallback;
            return (uint)value;
        }

    }
}

