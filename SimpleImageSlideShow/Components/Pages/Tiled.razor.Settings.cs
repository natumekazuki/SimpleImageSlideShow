using Microsoft.AspNetCore.Components;
using SimpleImageSlideShow.Services;
using System.Globalization;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private void OnDelayInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                DelaySeconds = Math.Max(1u, Math.Min(60u, v));
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

        private async Task OnVolumeInput(ChangeEventArgs e)
        {
            if (e.Value is string s && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                AudioVolumePercent = Math.Clamp(v, 0, 100);
                await ApplyAudioVolumeToJsAsync();
            }
        }

        private async Task SaveAndApplyAsync()
        {
            var settings = await SettingsService.LoadAsync();
            settings.DelaySeconds = DelaySeconds;
            // Fill target is implicit (100%); not persisted anymore
            settings.TiledMinScale = MinScale;
            settings.TiledMaxScale = MaxScale;
            settings.DirectoryPath = DirectoryPath;
            settings.BackgroundColor = BackgroundColor;
            settings.TiledCols = TiledCols;
            settings.MinTilePx = MinTilePx;
            settings.TiledReuseTtlSeconds = ReuseTtlSeconds;
            settings.RandomScaleTries = RandomScaleTries;
            settings.AudioVolumePercent = AudioVolumePercent;
            settings.ShowTiledClock = ShowClock;
            settings.AvoidTiledClockOverlap = AvoidClockOverlap;
            settings.TiledClockCorner = ClockCorner;
            settings.TiledClockScale = ClockScale;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            // keep panel size fixed; stop persisting size
            await SettingsService.SaveAsync(settings);
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
            var settings = await SettingsService.LoadAsync();
            settings.DirectoryPath = DirectoryPath;
            settings.BackgroundColor = BackgroundColor;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            await SettingsService.SaveAsync(settings);

            Nav.NavigateTo(Nav.Uri, forceLoad: true);
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
