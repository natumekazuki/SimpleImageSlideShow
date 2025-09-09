namespace SimpleImageSlideShow.Services
{
    public interface IWebViewHostService
    {
        string HostName { get; }
        void SetCore(object coreWebView2);
        void MapImagesFolder(string? folderPath);
    }
}

