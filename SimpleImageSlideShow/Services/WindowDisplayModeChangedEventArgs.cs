namespace SimpleImageSlideShow.Services
{
    public sealed class WindowDisplayModeChangedEventArgs : EventArgs
    {
        public WindowDisplayModeChangedEventArgs(WindowDisplayMode mode)
        {
            Mode = mode;
        }

        public WindowDisplayMode Mode { get; }
    }
}
