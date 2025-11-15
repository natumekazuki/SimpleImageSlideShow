using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Platform;
using Microsoft.UI.Windowing;
using SimpleImageSlideShow.Services;
using System.Threading;
using Windows.Graphics;

namespace SimpleImageSlideShow.Platforms.Windows
{
    internal sealed class WindowService : IWindowService
    {
        private readonly ISettingsService _settingsService;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private AppWindow? _appWindow;
        private bool _initialized;
        private WindowDisplayMode _currentMode = WindowDisplayMode.Windowed;
        private RectInt32? _lastWindowRect;
        private bool _initialModeApplied;
        private const int FixedWindowWidth = 1920;
        private const int FixedWindowHeight = 1080;

        public WindowService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        public WindowDisplayMode CurrentMode => _currentMode;

        public event EventHandler<WindowDisplayModeChangedEventArgs>? ModeChanged;

        public async Task InitializeAsync()
        {
            if (_initialized) return;
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized) return;
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var mauiWindow = Application.Current?.Windows.FirstOrDefault();
                    if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                        return;

                    var appWindow = nativeWindow.GetAppWindow();
                    if (appWindow is null) return;

                    AttachWindow(appWindow);
                }).ConfigureAwait(false);

                _initialized = _appWindow is not null;
                if (_initialized)
                {
                    await ApplyInitialModeFromSettingsAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task SetModeAsync(WindowDisplayMode mode)
        {
            await InitializeAsync().ConfigureAwait(false);
            await SetModeInternalAsync(mode).ConfigureAwait(false);
        }

        public async Task ToggleModeAsync()
        {
            var target = _currentMode == WindowDisplayMode.FullScreen
                ? WindowDisplayMode.Windowed
                : WindowDisplayMode.FullScreen;
            await SetModeAsync(target).ConfigureAwait(false);
        }

        public void Exit()
        {
            try
            {
                var mauiWindow = Application.Current?.Windows.FirstOrDefault();
                if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    nativeWindow.Close();
                    return;
                }
            }
            catch { }

            try { Application.Current?.Quit(); } catch { }
            try { Environment.Exit(0); } catch { }
        }

        private void AttachWindow(AppWindow appWindow)
        {
            _appWindow = appWindow;
            var workArea = GetWorkArea();
            _lastWindowRect = CreateFixedWindowRect(workArea);
            _appWindow.Changed += OnAppWindowChanged;
            UpdateModeFromPresenter(force: true);
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateModeFromPresenter();
        }

        private void EnterFullScreen()
        {
            if (_appWindow is null) return;

            var workArea = GetWorkArea();
            _lastWindowRect = CreateFixedWindowRect(workArea);
            _appWindow.MoveAndResize(workArea);
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        private void ExitFullScreen()
        {
            if (_appWindow is null) return;
            var workArea = GetWorkArea();
            var restoreRect = CreateFixedWindowRect(workArea);
            _lastWindowRect = restoreRect;

            _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (restoreRect.Width > 0 && restoreRect.Height > 0)
            {
                _appWindow.MoveAndResize(restoreRect);
            }
        }

        private void UpdateModeFromPresenter(bool force = false)
        {
            var mode = (_appWindow?.Presenter?.Kind == AppWindowPresenterKind.FullScreen)
                ? WindowDisplayMode.FullScreen
                : WindowDisplayMode.Windowed;

            if (!force && mode == _currentMode) return;

            _currentMode = mode;
            var handler = ModeChanged;
            if (handler is null) return;
            var args = new WindowDisplayModeChangedEventArgs(mode);
            MainThread.BeginInvokeOnMainThread(() => handler(this, args));
        }

        private async Task SetModeInternalAsync(WindowDisplayMode mode)
        {
            if (_appWindow is null) return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    if (mode == WindowDisplayMode.FullScreen)
                    {
                        EnterFullScreen();
                    }
                    else
                    {
                        ExitFullScreen();
                    }
                }
                catch
                {
                    // ignore
                }
            }).ConfigureAwait(false);
        }

        private async Task ApplyInitialModeFromSettingsAsync()
        {
            if (_initialModeApplied) return;
            _initialModeApplied = true;
            try
            {
                var settings = await _settingsService.LoadAsync().ConfigureAwait(false);
                var desired = ParseMode(settings.WindowDisplayMode);
                await SetModeInternalAsync(desired).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        private static WindowDisplayMode ParseMode(string? value)
        {
            return string.Equals(value, "Windowed", StringComparison.OrdinalIgnoreCase)
                ? WindowDisplayMode.Windowed
                : WindowDisplayMode.FullScreen;
        }

        private RectInt32 GetWorkArea()
        {
            if (_appWindow is null)
            {
                return new RectInt32(0, 0, 1280, 720);
            }
            var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
            return displayArea.WorkArea;
        }

        private static RectInt32 CreateFixedWindowRect(RectInt32 workArea)
        {
            var width = Math.Min(FixedWindowWidth, workArea.Width);
            var height = Math.Min(FixedWindowHeight, workArea.Height);
            var x = workArea.X + (workArea.Width - width) / 2;
            var y = workArea.Y + (workArea.Height - height) / 2;
            return new RectInt32(x, y, width, height);
        }
    }
}
