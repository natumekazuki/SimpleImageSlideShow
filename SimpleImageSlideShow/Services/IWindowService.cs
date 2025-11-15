namespace SimpleImageSlideShow.Services
{
    public interface IWindowService
    {
        WindowDisplayMode CurrentMode { get; }

        event EventHandler<WindowDisplayModeChangedEventArgs>? ModeChanged;

        Task InitializeAsync();
        Task SetModeAsync(WindowDisplayMode mode);
        Task ToggleModeAsync();
        void Exit();
    }
}
