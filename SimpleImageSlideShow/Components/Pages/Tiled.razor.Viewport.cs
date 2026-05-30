using Microsoft.JSInterop;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        [JSInvokable]
        public async Task OnResize(int w, int h)
        {
            ViewportW = Math.Max(1, w);
            ViewportH = Math.Max(1, h);
            RecomputeGrid();
            await InvokeAsync(StateHasChanged);
        }

        private async Task RefreshViewportAsync()
        {
            try
            {
                var viewport = await JS.InvokeAsync<ViewportSize>("window.app.getViewportSize");
                await OnResize((int)Math.Round(viewport.Width), (int)Math.Round(viewport.Height));
            }
            catch
            {
            }
        }

        private void RecomputeGrid()
        {
            // columns from current setting (square tiles), enforce min tile width via ColsMax
            var clamped = Math.Max(1, Math.Min(TiledCols, ColsMax));
            Cols = clamped;
            // tile size from width
            var s = ViewportW / Math.Max(1, Cols);
            // rows so that squares fit vertically
            Rows = Math.Max(1, (int)Math.Floor(ViewportH / s));
            // recompute s to fit height as well
            s = Math.Min(s, ViewportH / Math.Max(1, Rows));
            TileW = TileH = s;

            GridW = TileW * Cols;
            GridH = TileH * Rows;
            OffsetX = (ViewportW - GridW) / 2.0;
            OffsetY = (ViewportH - GridH) / 2.0;

            Occupied = new bool[Rows, Cols];
            Owners = new TiledItem?[Rows, Cols];
            ComputeClockReservedCells();
            UpdateClockOverlap();
            // reset items on recompute asリセットでOKの仕様
            Items.Clear();
            UsedPaths.Clear();
            _lastTickItem = null;
            InvalidatePlan();
        }

        [JSInvokable]
        public Task OnPanelSizeChanged(double width, double height)
        {
            // size persistence disabled; accept callback to avoid JS errors
            return Task.CompletedTask;
        }

    }
}
