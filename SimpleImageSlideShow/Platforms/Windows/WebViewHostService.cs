using Microsoft.Web.WebView2.Core;

namespace SimpleImageSlideShow.Platforms.Windows
{
    internal sealed class WebViewHostService : SimpleImageSlideShow.Services.IWebViewHostService
    {
        private CoreWebView2? _core;
        private string? _mappedFolder;

        public string HostName => "appimages.local";

        public void SetCore(object coreWebView2)
        {
            _core = coreWebView2 as CoreWebView2;
            if (_core is not null && _mappedFolder is not null)
            {
                TryMap(_mappedFolder);
            }
        }

        public void MapImagesFolder(string? folderPath)
        {
            _mappedFolder = folderPath;
            if (_core is null || string.IsNullOrWhiteSpace(folderPath)) return;
            TryMap(folderPath);
        }

        private void TryMap(string folderPath)
        {
            try
            {
                _core!.ClearVirtualHostNameToFolderMapping(HostName);
            }
            catch { }
            try
            {
                _core!.SetVirtualHostNameToFolderMapping(HostName, folderPath, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }
        }
    }
}

