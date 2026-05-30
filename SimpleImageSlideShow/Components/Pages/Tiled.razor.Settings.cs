using Microsoft.AspNetCore.Components;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private async Task RefreshSettingsProfilesAsync()
        {
            SettingsProfiles = await SettingsService.ListProfilesAsync();
            var activeProfile = SettingsProfiles.FirstOrDefault(profile => profile.IsActive);
            if (activeProfile is not null)
            {
                ActiveSettingsProfileId = activeProfile.Id;
                ActiveSettingsProfileName = activeProfile.Name;
            }
        }

        private void ApplySettingsToState(Models.AppSettings settings)
        {
            var delayRange = Models.DelayRange.Normalize(settings.MinDelaySeconds, settings.MaxDelaySeconds);
            MinDelaySeconds = delayRange.MinSeconds;
            MaxDelaySeconds = delayRange.MaxSeconds;
            MinScale = Math.Clamp(settings.TiledMinScale, 0.1, 1.0);
            MaxScale = Math.Clamp(settings.TiledMaxScale, 0.1, 1.0);
            if (MaxScale < MinScale) MaxScale = MinScale;
            DirectoryPath = settings.DirectoryPath;
            BackgroundColor = NormalizeBackgroundColor(settings.BackgroundColor);
            TiledCols = settings.TiledCols > 0 ? settings.TiledCols : 6;
            MinTilePx = settings.MinTilePx > 0 ? settings.MinTilePx : 128;
            ReuseTtlSeconds = settings.TiledReuseTtlSeconds > 0 ? settings.TiledReuseTtlSeconds : 120;
            RandomScaleTries = settings.RandomScaleTries > 0 ? settings.RandomScaleTries : 10;
            ShowClock = settings.ShowTiledClock;
            AvoidClockOverlap = settings.AvoidTiledClockOverlap;
            ClockCorner = NormalizeClockCorner(settings.TiledClockCorner);
            ClockScale = Math.Clamp(settings.TiledClockScale, 0.5, 2.0);
        }

        private Models.AppSettings CaptureSettingsFromState()
        {
            return new Models.AppSettings
            {
                MinDelaySeconds = MinDelaySeconds,
                MaxDelaySeconds = MaxDelaySeconds,
                TiledMinScale = MinScale,
                TiledMaxScale = MaxScale,
                DirectoryPath = DirectoryPath,
                BackgroundColor = BackgroundColor,
                TiledCols = TiledCols,
                MinTilePx = MinTilePx,
                TiledReuseTtlSeconds = ReuseTtlSeconds,
                RandomScaleTries = RandomScaleTries,
                ShowTiledClock = ShowClock,
                AvoidTiledClockOverlap = AvoidClockOverlap,
                TiledClockCorner = ClockCorner,
                TiledClockScale = ClockScale,
                WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed"
            };
        }

        private async Task SaveActiveProfileNameAsync()
        {
            if (!string.IsNullOrWhiteSpace(ActiveSettingsProfileId))
            {
                await SettingsService.RenameProfileAsync(ActiveSettingsProfileId, ActiveSettingsProfileName);
            }
        }

        private async Task SaveCurrentProfileAsync()
        {
            await SaveActiveProfileNameAsync();
            await SettingsService.SaveAsync(CaptureSettingsFromState());
            await RefreshSettingsProfilesAsync();
        }

        private void OnProfileNameInput(ChangeEventArgs e)
        {
            ActiveSettingsProfileName = e.Value?.ToString() ?? string.Empty;
        }

        private async Task OnActiveProfileChanged(ChangeEventArgs e)
        {
            var profileId = e.Value?.ToString();
            if (string.IsNullOrWhiteSpace(profileId) || string.Equals(profileId, ActiveSettingsProfileId, StringComparison.Ordinal))
            {
                return;
            }

            IsProfileChanging = true;
            try
            {
                await StopAsync();
                await SaveCurrentProfileAsync();
                await SettingsService.SetActiveProfileAsync(profileId);
                await PrepareActiveProfileImagesAsync();
                ReloadTiledPage();
            }
            finally
            {
                IsProfileChanging = false;
            }
        }

        private async Task CreateProfileAsync()
        {
            IsProfileChanging = true;
            try
            {
                var profileName = GetNewProfileName();
                await SettingsService.CreateProfileAsync(profileName, CaptureSettingsFromState(), isActive: true);
                await RefreshSettingsProfilesAsync();
            }
            finally
            {
                IsProfileChanging = false;
            }
        }

        private async Task DeleteActiveProfileAsync()
        {
            if (!CanDeleteActiveProfile || string.IsNullOrWhiteSpace(ActiveSettingsProfileId)) return;

            IsProfileChanging = true;
            try
            {
                await StopAsync();
                if (await SettingsService.DeleteProfileAsync(ActiveSettingsProfileId))
                {
                    await PrepareActiveProfileImagesAsync();
                    ReloadTiledPage();
                }
            }
            finally
            {
                IsProfileChanging = false;
            }
        }

        private async Task PrepareActiveProfileImagesAsync()
        {
            var activeProfile = await SettingsService.LoadActiveProfileAsync();
            var directoryPath = activeProfile.Settings.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return;

            ImageService.LoadImages(directoryPath);
            WebViewHost.MapImagesFolder(directoryPath);
        }

        private void ReloadTiledPage()
        {
            Nav.NavigateTo("/", forceLoad: true);
        }

        private string GetNewProfileName()
        {
            const string baseName = "New Profile";
            var names = SettingsProfiles.Select(profile => profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;

            for (var i = 2; i < 1000; i++)
            {
                var candidate = $"{baseName} {i}";
                if (!names.Contains(candidate)) return candidate;
            }

            return $"{baseName} {DateTime.Now:yyyyMMddHHmmss}";
        }

        private void OnMinDelayInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                MinDelaySeconds = Math.Min(60u, v);
                if (MaxDelaySeconds < MinDelaySeconds) MaxDelaySeconds = MinDelaySeconds;
            }
        }

        private void OnMaxDelayInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                MaxDelaySeconds = Math.Max(1u, Math.Min(60u, v));
                if (MaxDelaySeconds < MinDelaySeconds) MinDelaySeconds = MaxDelaySeconds;
            }
        }

        // Fill target control removed: always aim to fully occupy the grid

        private void OnMinScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MinScale = Math.Clamp(v / 100.0, 0.1, 1.0);
                if (MaxScale < MinScale) MaxScale = MinScale;
            }
        }

        private void OnMaxScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MaxScale = Math.Clamp(v / 100.0, 0.1, 1.0);
                if (MaxScale < MinScale) MinScale = MaxScale;
            }
        }

        private void OnBackgroundColorSelected(string value)
        {
            var next = NormalizeBackgroundColor(value);
            if (!string.Equals(next, BackgroundColor, StringComparison.OrdinalIgnoreCase))
            {
                BackgroundColor = next;
            }
        }

        private static string NormalizeBackgroundColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultBackgroundColor;
            var trimmed = value.Trim();
            return trimmed.StartsWith('#') ? trimmed : $"#{trimmed}";
        }

        private void OnColsInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                TiledCols = Math.Clamp(v, 1, ColsMax > 0 ? ColsMax : 200);
                RecomputeGrid();
                StateHasChanged();
            }
        }

        private void OnMinTileInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MinTilePx = Math.Clamp(v, 64, 512);
                RecomputeGrid();
                StateHasChanged();
            }
        }

        private void OnRandomScaleTriesInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                RandomScaleTries = Math.Max(1u, Math.Min(500u, v));
            }
        }

        private async Task SaveAndApplyAsync()
        {
            await SaveCurrentProfileAsync();
            await StartAsync();
            try { await EnsurePlanAsync(); } catch { }
        }

        private async Task ChooseAndApplyFolderAsync()
        {
            var directoryPath = await FolderPickerService.SelectDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            await StopAsync();

            DirectoryPath = directoryPath;
            ImageService.LoadImages(directoryPath);
            WebViewHost.MapImagesFolder(directoryPath);
            await SaveCurrentProfileAsync();

            ReloadTiledPage();
        }

        private async Task ToggleWindowModeAsync()
        {
            if (IsWindowModeChanging) return;
            IsWindowModeChanging = true;
            try
            {
                await WindowService.ToggleModeAsync();
            }
            catch
            {
                // swallow; UI will refresh via event if successful
            }
            finally
            {
                IsWindowModeChanging = false;
                await RefreshViewportAsync();
                await InvokeAsync(StateHasChanged);
            }
        }

        private async void OnWindowModeChanged(object? sender, WindowDisplayModeChangedEventArgs e)
        {
            UpdateWindowMode(e.Mode);
            await RefreshViewportAsync();
        }

        private void UpdateWindowMode(WindowDisplayMode mode, bool force = false)
        {
            var isFull = mode == WindowDisplayMode.FullScreen;
            if (!force && isFull == IsFullScreen) return;
            IsFullScreen = isFull;
            if (!force)
            {
                _ = InvokeAsync(StateHasChanged);
            }
        }

        private void ExitApp() => WindowService.Exit();

    }
}
