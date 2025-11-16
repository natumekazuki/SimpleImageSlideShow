using Microsoft.AspNetCore.Components;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;
using Windows.Storage.Pickers;
using System.IO;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Home : IDisposable
    {
        [Inject]
        public required IImageService ImageService { get; init; }

        [Inject]
        public required ISettingsService SettingsService { get; init; }

        [Inject]
        public required NavigationManager Nav { get; init; }

        [Inject]
        public required IWindowService WindowService { get; init; }

        [Inject]
        public required IWebViewHostService WebViewHost { get; init; }

        private List<IImageEntity> Slides { get; set; } = [];

        private uint DelaySeconds { get; set; } = DefaultDelaySeconds;

        private uint AnimationDelaySeconds => this.DelaySeconds * this.ImageCount;

        private uint ImageCount { get; set; } = DefaultImageCount;
        private string BackgroundColor { get; set; } = DefaultBackgroundColor;
        private string? DirectoryPath { get; set; }
        private bool IsFullScreen { get; set; }
        private bool IsWindowModeChanging { get; set; }

        // fixed slider bounds
        private const uint DelayMax = 60;
        private const uint CountMax = 10;

        private const uint DefaultImageCount = 3;

        private const uint DefaultDelaySeconds = 5;
        private const string DefaultBackgroundColor = "#D3D3D3";

        private string BackgroundStyle => $"--app-background-color:{BackgroundColor};background-color:var(--app-background-color);";

        private static async Task<string> SelectDirectoryAsync()
        {
            var mauiWindow = Application.Current?.Windows[0].Handler.PlatformView as Microsoft.UI.Xaml.Window;
            if (mauiWindow == null) return string.Empty;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mauiWindow);
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folderPicked = await picker.PickSingleFolderAsync();
            return folderPicked is not null ? folderPicked.Path : string.Empty;
        }

        // non-overlapping periodic loop
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private async Task RestartLoopAsync()
        {
            await StopLoopAsync();

            // sanitize
            if (DelaySeconds < 1) DelaySeconds = 1;
            if (ImageCount < 1) ImageCount = 1;

            // keep existing slides; only update tick period

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var period = TimeSpan.FromSeconds(DelaySeconds);
            var timer = new PeriodicTimer(period);
            _loopTask = Task.Run(async () =>
            {
                try
                {
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        await InvokeAsync(async () =>
                        {
                            await this.ReloadImageAsync();
                            this.StateHasChanged();
                        });
                    }
                }
                catch (OperationCanceledException) { }
                finally { timer.Dispose(); }
            }, token);
        }

        private async Task StopLoopAsync()
        {
            try
            {
                _cts?.Cancel();
                if (_loopTask is not null)
                {
                    await Task.WhenAny(_loopTask, Task.Delay(500));
                }
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _loopTask = null;
            }
        }

        private async Task ChooseAndApplyFolderAsync()
        {
            var directoryPath = await SelectDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            await this.StopLoopAsync();

            DirectoryPath = directoryPath;
            ImageService.LoadImages(directoryPath);
            WebViewHost.MapImagesFolder(directoryPath);
            var settings = await SettingsService.LoadAsync();
            settings.DelaySeconds = this.DelaySeconds;
            settings.ImageCount = this.ImageCount;
            settings.DirectoryPath = this.DirectoryPath;
            settings.BackgroundColor = this.BackgroundColor;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            await SettingsService.SaveAsync(settings);

            Nav.NavigateTo(Nav.Uri, forceLoad: true);
        }

        private async Task EnsureFolderLoadedAsync()
        {
            if (!string.IsNullOrWhiteSpace(DirectoryPath) && Directory.Exists(DirectoryPath))
            {
                ImageService.LoadImages(DirectoryPath);
                WebViewHost.MapImagesFolder(DirectoryPath);
                return;
            }
            await ChooseAndApplyFolderAsync();
        }

        private async Task ReloadImageAsync()
        {
            var imagePath = ImageService.GetRandomImagePath();
            var imageEntity = await ImageService.LoadImageEntityAsync(imagePath);
            if (imageEntity is null) return;
            Slides.Add(imageEntity);
            _ = RemoveAfterAsync(imageEntity, AnimationDelaySeconds);
        }

        private async Task RemoveAfterAsync(IImageEntity entity, uint seconds)
        {
            var token = _cts?.Token ?? CancellationToken.None;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) { return; }
            await InvokeAsync(() =>
            {
                Slides.Remove(entity);
                StateHasChanged();
            });
        }

        private void ExitApp()
        {
            WindowService.Exit();
        }

        private void OnDelayInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                DelaySeconds = Math.Max(1u, v);
            }
        }

        private void OnCountInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                ImageCount = Math.Max(1u, v);
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

        private async Task SaveAndApplyAsync()
        {
            var settings = await SettingsService.LoadAsync();
            settings.DelaySeconds = this.DelaySeconds;
            settings.ImageCount = this.ImageCount;
            settings.DirectoryPath = this.DirectoryPath;
            settings.LastMode = "Slide";
            settings.BackgroundColor = this.BackgroundColor;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            await SettingsService.SaveAsync(settings);
            await RestartLoopAsync();
            await InvokeAsync(StateHasChanged);
        }

        private string GetSrc(IImageEntity entity)
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) return entity.ImageUrl;
            string rel = Path.GetRelativePath(DirectoryPath, entity.FilePath).Replace('\\', '/');
            return $"https://{WebViewHost.HostName}/{rel}";
        }

        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();

            await WindowService.InitializeAsync();
            UpdateWindowMode(WindowService.CurrentMode, force: true);
            WindowService.ModeChanged += OnWindowModeChanged;

            // load settings
            var settings = await SettingsService.LoadAsync();
            if (string.Equals(settings.LastMode, "Tiled", StringComparison.OrdinalIgnoreCase))
            {
                Nav.NavigateTo("/tiled");
                return;
            }
            this.DelaySeconds = settings.DelaySeconds > 0 ? settings.DelaySeconds : DefaultDelaySeconds;
            this.ImageCount = settings.ImageCount > 0 ? settings.ImageCount : DefaultImageCount;
            this.BackgroundColor = NormalizeBackgroundColor(settings.BackgroundColor);
            this.DirectoryPath = settings.DirectoryPath;

            await EnsureFolderLoadedAsync();
            await this.ReloadImageAsync();
            await RestartLoopAsync();
        }

        private async Task SwitchMode(string mode)
        {
            var settings = await SettingsService.LoadAsync();
            settings.LastMode = mode;
            await SettingsService.SaveAsync(settings);
            if (string.Equals(mode, "Tiled", StringComparison.OrdinalIgnoreCase))
                Nav.NavigateTo("/tiled");
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
                // ignore failures; state change event will keep UI honest
            }
            finally
            {
                IsWindowModeChanging = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private void OnWindowModeChanged(object? sender, WindowDisplayModeChangedEventArgs e)
        {
            UpdateWindowMode(e.Mode);
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

        public void Dispose()
        {
            _ = StopLoopAsync();
            ImageService.Dispose();
            WindowService.ModeChanged -= OnWindowModeChanged;
        }
    }
}
