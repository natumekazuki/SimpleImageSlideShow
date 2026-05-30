using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        protected override async Task OnInitializedAsync()
        {
            await WindowService.InitializeAsync();
            UpdateWindowMode(WindowService.CurrentMode, force: true);
            WindowService.ModeChanged += OnWindowModeChanged;

            var activeProfile = await SettingsService.LoadActiveProfileAsync();
            ActiveSettingsProfileId = activeProfile.Id;
            ActiveSettingsProfileName = activeProfile.Name;
            await RefreshSettingsProfilesAsync();
            ApplySettingsToState(activeProfile.Settings);

            if (!string.IsNullOrWhiteSpace(DirectoryPath) && Directory.Exists(DirectoryPath))
            {
                ImageService.LoadImages(DirectoryPath);
                WebViewHost.MapImagesFolder(DirectoryPath);
            }
            else
            {
                await ChooseAndApplyFolderAsync();
            }

            // Initialize clock text and periodic update
            UpdateClockText();
            try
            {
                var due = TimeSpan.FromSeconds(Math.Max(1, 60 - DateTime.Now.Second));
                _clockTimer = new Timer(_ =>
                {
                    try
                    {
                        UpdateClockText();
                        _ = InvokeAsync(StateHasChanged);
                    }
                    catch { }
                }, null, due, TimeSpan.FromSeconds(30));
            }
            catch { }
        }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                try
                {
                    _selfRef = DotNetObjectReference.Create(this);
                    _resizeObj = await JS.InvokeAsync<IJSObjectReference>("window.app.addResizeListener", _selfRef);
                }
                catch { }

                // 初回はディレイ無視で1枚挿入（グリッド初期化を待つ）
                await WaitForGridReadyAsync(TimeSpan.FromMilliseconds(800));
                try
                {
                    await StepAsync();
                    StateHasChanged();
                }
                catch { }

                try { await EnsurePlanAsync(); } catch { }
                await StartAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            try { if (_resizeObj is not null) await _resizeObj.InvokeVoidAsync("dispose"); } catch { }
            try { _selfRef?.Dispose(); } catch { }
            try { _clockTimer?.Dispose(); } catch { }
            CancelClockLayoutUpdate();
            WindowService.ModeChanged -= OnWindowModeChanged;
            ImageService.Dispose();
        }

    }
}
