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
            return (await LoadActiveProfileAsync()).Settings;
        }

        public async Task<SettingsProfile> LoadActiveProfileAsync()
        {
            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);
                return await LoadActiveProfileAsync(connection) ?? CreateFallbackProfile();
            }
            catch
            {
                // ignore and fall back
                return CreateFallbackProfile();
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task<IReadOnlyList<SettingsProfileSummary>> ListProfilesAsync()
        {
            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);

                var profiles = new List<SettingsProfileSummary>();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, name, is_active
                    FROM settings_profiles
                    ORDER BY is_active DESC, updated_at DESC, name COLLATE NOCASE ASC;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    profiles.Add(new SettingsProfileSummary(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));
                }

                return profiles;
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
                var activeProfile = await GetActiveProfileSummaryAsync(connection);
                var profileId = activeProfile?.Id ?? Guid.NewGuid().ToString("D");
                var name = activeProfile?.Name ?? DefaultProfileName;
                await UpsertProfileAsync(connection, profileId, name, isActive: true, Normalize(settings));
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task<string> CreateProfileAsync(string name, AppSettings settings, bool isActive)
        {
            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);

                var profileId = Guid.NewGuid().ToString("D");
                await UpsertProfileAsync(connection, profileId, NormalizeProfileName(name), isActive, Normalize(settings));
                return profileId;
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task RenameProfileAsync(string profileId, string name)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return;

            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);

                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE settings_profiles
                    SET name = $name,
                        updated_at = $updated_at
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", profileId);
                command.Parameters.AddWithValue("$name", NormalizeProfileName(name));
                command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task SetActiveProfileAsync(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return;

            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);

                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
                if (!await ProfileExistsAsync(connection, transaction, profileId))
                {
                    await transaction.RollbackAsync();
                    return;
                }

                await SetActiveProfileAsync(connection, profileId, transaction);
                await transaction.CommitAsync();
            }
            finally
            {
                DatabaseLock.Release();
            }
        }

        public async Task<bool> DeleteProfileAsync(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return false;

            await DatabaseLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_settingsDirectory);
                await using var connection = await OpenConnectionAsync();
                await EnsureDatabaseAsync(connection);
                await EnsureInitialProfileAsync(connection);

                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
                var count = await CountProfilesAsync(connection, transaction);
                if (count <= 1)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                var wasActive = await IsActiveProfileAsync(connection, transaction, profileId);
                int deletedRows;
                await using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "DELETE FROM settings_profiles WHERE id = $id;";
                    deleteCommand.Parameters.AddWithValue("$id", profileId);
                    deletedRows = await deleteCommand.ExecuteNonQueryAsync();
                }

                if (deletedRows == 0)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                if (wasActive)
                {
                    await using var selectCommand = connection.CreateCommand();
                    selectCommand.Transaction = transaction;
                    selectCommand.CommandText = """
                        SELECT id
                        FROM settings_profiles
                        ORDER BY updated_at DESC
                        LIMIT 1;
                        """;
                    var nextProfileId = await selectCommand.ExecuteScalarAsync() as string;
                    if (!string.IsNullOrWhiteSpace(nextProfileId))
                    {
                        await SetActiveProfileAsync(connection, nextProfileId, transaction);
                    }
                }

                await transaction.CommitAsync();
                return true;
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

        private static async Task<SettingsProfileSummary?> GetActiveProfileSummaryAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, is_active FROM settings_profiles WHERE is_active = 1 LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new SettingsProfileSummary(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0);
        }

        private static async Task<SettingsProfile?> LoadActiveProfileAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
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
                    background_color
                FROM settings_profiles
                WHERE is_active = 1
                LIMIT 1;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            var settings = Normalize(new AppSettings
            {
                DirectoryPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                MinDelaySeconds = ToUInt32(reader.GetInt64(4), 5),
                MaxDelaySeconds = ToUInt32(reader.GetInt64(5), 5),
                WindowDisplayMode = reader.GetString(6),
                TiledMinScale = reader.GetDouble(7),
                TiledMaxScale = reader.GetDouble(8),
                TiledCols = ToInt32(reader.GetInt64(9), 6),
                MinTilePx = ToInt32(reader.GetInt64(10), 128),
                TiledReuseTtlSeconds = ToInt32(reader.GetInt64(11), 120),
                RandomScaleTries = ToUInt32(reader.GetInt64(12), 10),
                ShowTiledClock = reader.GetInt64(13) != 0,
                TiledClockCorner = reader.GetString(14),
                TiledClockScale = reader.GetDouble(15),
                AvoidTiledClockOverlap = reader.GetInt64(16) != 0,
                BackgroundColor = reader.GetString(17)
            });
            return new SettingsProfile(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, settings);
        }

        private static async Task SetActiveProfileAsync(SqliteConnection connection, string profileId, SqliteTransaction? transaction = null)
        {
            await using (var deactivateCommand = connection.CreateCommand())
            {
                deactivateCommand.Transaction = transaction;
                deactivateCommand.CommandText = "UPDATE settings_profiles SET is_active = 0;";
                await deactivateCommand.ExecuteNonQueryAsync();
            }

            await using (var activateCommand = connection.CreateCommand())
            {
                activateCommand.Transaction = transaction;
                activateCommand.CommandText = """
                    UPDATE settings_profiles
                    SET is_active = 1,
                        updated_at = $updated_at
                    WHERE id = $id;
                    """;
                activateCommand.Parameters.AddWithValue("$id", profileId);
                activateCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                await activateCommand.ExecuteNonQueryAsync();
            }
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

        private static async Task<int> CountProfilesAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM settings_profiles;";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static async Task<bool> IsActiveProfileAsync(SqliteConnection connection, SqliteTransaction transaction, string profileId)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT is_active FROM settings_profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", profileId);
            var value = await command.ExecuteScalarAsync();
            return value is not null && value != DBNull.Value && Convert.ToInt64(value) != 0;
        }

        private static async Task<bool> ProfileExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string profileId)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM settings_profiles WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", profileId);
            var value = await command.ExecuteScalarAsync();
            return value is not null && value != DBNull.Value;
        }

        private static SettingsProfile CreateFallbackProfile()
        {
            return new SettingsProfile(Guid.Empty.ToString("D"), DefaultProfileName, true, new AppSettings());
        }

        private static string NormalizeProfileName(string name)
        {
            var trimmed = name.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return DefaultProfileName;
            return trimmed.Length <= 80 ? trimmed : trimmed[..80];
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

