namespace SimpleImageSlideShow
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
            this.blazorWebView.HandlerChanged += BlazorWebView_HandlerChanged;
        }

#if WINDOWS
        private async void BlazorWebView_HandlerChanged(object? sender, EventArgs e)
        {
            try
            {
                var handler = this.blazorWebView.Handler;
                var services = handler?.MauiContext?.Services;
                if (handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2 && services is not null)
                {
                    if (wv2.CoreWebView2 is null)
                    {
                        await wv2.EnsureCoreWebView2Async();
                    }

                    var host = (Services.IWebViewHostService?)services.GetService(typeof(Services.IWebViewHostService));
                    host?.SetCore(wv2.CoreWebView2!);

                    var settingsSvc = (Services.ISettingsService?)services.GetService(typeof(Services.ISettingsService));
                    if (settingsSvc is not null)
                    {
                        var settings = await settingsSvc.LoadAsync();
                        if (!string.IsNullOrWhiteSpace(settings.DirectoryPath))
                        {
                            host?.MapImagesFolder(settings.DirectoryPath);
                        }
                    }
                }
            }
            catch { }
        }
#endif
    }
}
