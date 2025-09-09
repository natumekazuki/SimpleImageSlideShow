using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled : IAsyncDisposable
    {
        [Inject] public required IImageService ImageService { get; init; }
        [Inject] public required ISettingsService SettingsService { get; init; }
        [Inject] public required IWindowService WindowService { get; init; }
        [Inject] public required IWebViewHostService WebViewHost { get; init; }
        [Inject] public required NavigationManager Nav { get; init; }
        [Inject] public required IJSRuntime JS { get; init; }

        private ElementReference ContainerRef;

        private record TiledItem
        {
            public Guid Id { get; } = Guid.NewGuid();
            public required string Path { get; init; }
            public int Col { get; set; }
            public int Row { get; set; }
            public int ColSpan { get; set; }
            public int RowSpan { get; set; }
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Removing { get; set; }
            public double Scale { get; set; } = 1.0;
            public double ImgWidth { get; set; }
            public double ImgHeight { get; set; }
            public required string Src { get; init; }
        }

        private List<TiledItem> Items { get; set; } = [];
        private HashSet<string> UsedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        private uint DelaySeconds { get; set; } = 5;
        private int FillTargetPercent { get; set; } = 80;
        private double MinScale { get; set; } = 0.5;
        private string? DirectoryPath { get; set; }
        private string HostName => WebViewHost.HostName;
        private int MinTilePx = 128; // 最小タイル幅（px）
        private int ColsMax => (int)Math.Max(1, Math.Floor(ViewportW / Math.Max(1, MinTilePx)));
        private double PanelWidth { get; set; } = 560;
        private double PanelHeight { get; set; } = 260;

        // Grid state
        private int TiledCols = 6;
        private int Cols = 2;
        private int Rows = 2;
        private double TileW;
        private double TileH;
        private double OffsetX;
        private double OffsetY;
        private double GridW;
        private double GridH;
        private double ViewportW;
        private double ViewportH;
        private bool[,]? Occupied;
        private TiledItem?[,]? Owners;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private IJSObjectReference? _resizeObj;
        private DotNetObjectReference<Tiled>? _selfRef;
        private readonly Dictionary<string, string> _dataUrlCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _dataUrlLru = new();
        private const int DataUrlCacheCapacity = 256;

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

        protected override async Task OnInitializedAsync()
        {
            var settings = await SettingsService.LoadAsync();
            DelaySeconds = settings.DelaySeconds > 0 ? settings.DelaySeconds : 5;
            FillTargetPercent = Math.Clamp(settings.TiledFillTargetPercent, 70, 100);
            MinScale = Math.Clamp(settings.TiledMinScale, 0.1, 1.0);
            DirectoryPath = settings.DirectoryPath;
            TiledCols = settings.TiledCols > 0 ? settings.TiledCols : 6;
            MinTilePx = settings.MinTilePx > 0 ? settings.MinTilePx : 128;
            PanelWidth = 560;
            PanelHeight = 260;

            if (!string.IsNullOrWhiteSpace(DirectoryPath) && Directory.Exists(DirectoryPath))
            {
                ImageService.LoadImages(DirectoryPath);
                WebViewHost.MapImagesFolder(DirectoryPath);
            }
            else
            {
                await ChooseAndApplyFolderAsync();
            }
        }

        [JSInvokable]
        public async Task OnResize(int w, int h)
        {
            ViewportW = Math.Max(1, w);
            ViewportH = Math.Max(1, h);
            RecomputeGrid();
            if (!_primed)
            {
                await PrimeInitialAsync();
                _primed = true;
            }
            await InvokeAsync(StateHasChanged);
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
            // reset items on recompute asリセットでOKの仕様
            Items.Clear();
            UsedPaths.Clear();
        }

        private bool _primed = false;

        private async Task PrimeInitialAsync()
        {
            // Fill up to FillTarget% quickly on first render (cap to 50 iterations)
            int safeguard = 50;
            while (safeguard-- > 0 && OccupancyPercent() < FillTargetPercent - 0.01)
            {
                var ok = await AddOneAsync();
                if (!ok)
                {
                    await ClearAndAddOneGuaranteedAsync();
                }
            }
        }

        private async Task StartAsync()
        {
            await StopAsync();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var period = TimeSpan.FromSeconds(Math.Max(1, DelaySeconds));
            var timer = new PeriodicTimer(period);

            _loopTask = Task.Run(async () =>
            {
                try
                {
                    while (await timer.WaitForNextTickAsync(token))
                    {
                        await InvokeAsync(async () =>
                        {
                            await StepAsync();
                            StateHasChanged();
                        });
                    }
                }
                catch (OperationCanceledException) { }
                finally { timer.Dispose(); }
            }, token);
        }

        private async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();
                if (_loopTask is not null)
                    await Task.WhenAny(_loopTask, Task.Delay(500));
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _loopTask = null;
            }
        }

        private double OccupancyPercent()
        {
            if (Occupied is null) return 0;
            int used = 0;
            foreach (var it in Items) used += it.ColSpan * it.RowSpan;
            var total = Cols * Rows;
            return total == 0 ? 0 : (100.0 * used / total);
        }

        private async Task StepAsync()
        {
            if (Occupied is null) return;
            var occ = OccupancyPercent();
            if (occ < FillTargetPercent - 0.01)
            {
                var added = await AddOneAsync();
                if (!added)
                {
                    // Ensure progress: clear overlapping items to place one at >= MinScale
                    await ClearAndAddOneGuaranteedAsync();
                }
            }
            else if (Items.Count > 0)
            {
                // Guarantee change: clear and place one
                await ClearAndAddOneGuaranteedAsync();
            }
        }

        // Replace phase now uses ClearAndAddOneGuaranteedAsync

        private async Task<bool> AddOneAsync()
        {
            var imagePath = await GetRandomUnusedPathAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return false;
            var entity = await ImageService.LoadImageEntityAsync(imagePath);
            if (entity is null) return false;

            var baseFit = GetBaseFitScale(entity);
            var rand = Random.Shared.NextDouble() * (1.0 - MinScale) + MinScale; // [MinScale,1]
            var scale = baseFit * rand;
            if (!TryPlaceScaled(entity, baseFit, ref rand, out var item))
            {
                return false;
            }

            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(entity.FilePath);
            return true;
        }

        private bool TryPlaceScaled(IImageEntity entity, double baseFit, ref double randScale, out TiledItem item)
        {
            item = default!;
            var attempts = 6;
            while (attempts-- > 0)
            {
                if (randScale < MinScale) randScale = MinScale;
                var scale = baseFit * randScale;
                var sw = entity.Width * scale;
                var sh = entity.Height * scale;

                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                int maxCols = Math.Min(Cols, reqCols + 2);
                int maxRows = Math.Min(Rows, reqRows + 2);

                for (int rs = reqRows; rs <= maxRows; rs++)
                {
                    for (int cs = reqCols; cs <= maxCols; cs++)
                    {
                        if (TryPlace(rs, cs, out var r, out var c))
                        {
                            item = new TiledItem
                            {
                                Path = entity.FilePath,
                                Row = r,
                                Col = c,
                                RowSpan = rs,
                                ColSpan = cs,
                                Left = OffsetX + c * TileW,
                                Top = OffsetY + r * TileH,
                                Width = cs * TileW,
                                Height = rs * TileH,
                                Scale = scale,
                                ImgWidth = sw,
                                ImgHeight = sh,
                                Src = BuildVirtualHostUrl(entity.FilePath)
                            };
                            return true;
                        }
                    }
                }

                if (randScale <= MinScale + 0.001) break;
                randScale = Math.Max(MinScale, randScale * 0.85);
            }
            return false;
        }

        // Find best rectangle by clearing minimal overlaps and place one image at >= MinScale
        private async Task ClearAndAddOneGuaranteedAsync()
        {
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathAsync();
                if (string.IsNullOrWhiteSpace(imagePath)) return;
                var entity = await ImageService.LoadImageEntityAsync(imagePath);
                if (entity is null) continue;

                // choose scale in [MinScale,1]
                var baseFit = GetBaseFitScale(entity);
                var rand = Random.Shared.NextDouble() * (1.0 - MinScale) + MinScale;
                var scale = baseFit * rand;
                var sw = entity.Width * scale;
                var sh = entity.Height * scale;
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                if (reqCols > Cols || reqRows > Rows)
                {
                    // image too large for grid at MinScale; try another image
                    continue;
                }

                if (TryPlace(reqRows, reqCols, out var rFree, out var cFree))
                {
                    var itemFree = new TiledItem
                    {
                        Path = entity.FilePath,
                        Row = rFree,
                        Col = cFree,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Left = OffsetX + cFree * TileW,
                        Top = OffsetY + rFree * TileH,
                        Width = reqCols * TileW,
                        Height = reqRows * TileH,
                        Scale = scale,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(entity.FilePath)
                    };
                    FillCells(rFree, cFree, reqRows, reqCols, true);
                    SetOwners(itemFree, true);
                    Items.Add(itemFree);
                    UsedPaths.Add(entity.FilePath);
                    return;
                }

                // not free: find position that requires minimal removals
                if (FindBestRectToClear(reqRows, reqCols, out int r, out int c, out var toRemove))
                {
                    // fade-out overlapped items before removal
                    foreach (var it in toRemove)
                    {
                        it.Removing = true;
                    }
                    StateHasChanged();
                    await Task.Delay(300);
                    foreach (var it in toRemove)
                    {
                        FillCells(it.Row, it.Col, it.RowSpan, it.ColSpan, false);
                        SetOwners(it, false);
                        Items.Remove(it);
                        UsedPaths.Remove(it.Path);
                    }
                    var item = new TiledItem
                    {
                        Path = entity.FilePath,
                        Row = r,
                        Col = c,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Left = OffsetX + c * TileW,
                        Top = OffsetY + r * TileH,
                        Width = reqCols * TileW,
                        Height = reqRows * TileH,
                        Scale = scale,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(entity.FilePath)
                    };
                    FillCells(r, c, reqRows, reqCols, true);
                    SetOwners(item, true);
                    Items.Add(item);
                    UsedPaths.Add(entity.FilePath);
                    return;
                }
            }
        }

        private void SetOwners(TiledItem item, bool set)
        {
            if (Owners is null) return;
            for (int r = item.Row; r < item.Row + item.RowSpan; r++)
            {
                for (int c = item.Col; c < item.Col + item.ColSpan; c++)
                {
                    Owners[r, c] = set ? item : null;
                }
            }
        }

        private double GetBaseFitScale(IImageEntity entity)
        {
            // If original is larger than grid area, scale down to fit within grid, else 1.0
            var sx = GridW / Math.Max(1.0, entity.Width);
            var sy = GridH / Math.Max(1.0, entity.Height);
            var fit = Math.Min(1.0, Math.Min(sx, sy));
            return double.IsFinite(fit) && fit > 0 ? fit : 1.0;
        }

        private bool FindBestRectToClear(int reqRows, int reqCols, out int bestR, out int bestC, out List<TiledItem> toRemove)
        {
            bestR = bestC = -1;
            toRemove = new List<TiledItem>();
            int bestCount = int.MaxValue;
            int totalPositions = Math.Max(1, (Rows - reqRows + 1) * (Cols - reqCols + 1));
            int budget = GetProbeLimit(totalPositions);
            var sampled = new HashSet<int>();

            // If positions are few, scan all; otherwise random sampling within budget
            if (totalPositions <= budget)
            {
                for (int r = 0; r <= Rows - reqRows; r++)
                {
                    for (int c = 0; c <= Cols - reqCols; c++)
                    {
                        var overlaps = GetOverlaps(r, c, reqRows, reqCols);
                        int count = overlaps.Count;
                        if (count < bestCount)
                        {
                            bestCount = count;
                            bestR = r;
                            bestC = c;
                            toRemove = overlaps;
                            if (bestCount == 0) return true;
                        }
                    }
                }
                return bestR >= 0;
            }
            else
            {
                for (int i = 0; i < budget; i++)
                {
                    int idx;
                    do { idx = Random.Shared.Next(totalPositions); } while (!sampled.Add(idx));
                    int r = idx / Math.Max(1, (Cols - reqCols + 1));
                    int c = idx % Math.Max(1, (Cols - reqCols + 1));
                    var overlaps = GetOverlaps(r, c, reqRows, reqCols);
                    int count = overlaps.Count;
                    if (count < bestCount)
                    {
                        bestCount = count;
                        bestR = r;
                        bestC = c;
                        toRemove = overlaps;
                        if (bestCount == 0) return true;
                    }
                }
                return bestR >= 0;
            }
        }

        private int GetProbeLimit(int totalPositions)
        {
            var occ = OccupancyPercent();
            // Budget scales with grid size but caps for N100-class CPU
            int baseBudget = occ < 50 ? 300 : occ < 80 ? 200 : 120;
            return Math.Min(totalPositions, baseBudget);
        }

        private List<TiledItem> GetOverlaps(int row, int col, int rowSpan, int colSpan)
        {
            var set = new HashSet<TiledItem>();
            if (Owners is null) return new List<TiledItem>();
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    var it = Owners[r, c];
                    if (it is not null) set.Add(it);
                }
            }
            return set.ToList();
        }

        private bool TryPlace(int rowSpan, int colSpan, out int row, out int col)
        {
            row = col = 0;
            if (Occupied is null) return false;
            var candidates = new List<(int r, int c)>();
            for (int r = 0; r <= Rows - rowSpan; r++)
            {
                for (int c = 0; c <= Cols - colSpan; c++)
                {
                    if (CanPlace(r, c, rowSpan, colSpan)) candidates.Add((r, c));
                }
            }
            if (candidates.Count == 0) return false;
            var pick = candidates[Random.Shared.Next(candidates.Count)];
            row = pick.r; col = pick.c; return true;
        }

        private bool CanPlace(int row, int col, int rowSpan, int colSpan)
        {
            if (Occupied is null) return false;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    if (r < 0 || r >= Rows || c < 0 || c >= Cols) return false;
                    if (Occupied[r, c]) return false;
                }
            }
            return true;
        }

        private void FillCells(int row, int col, int rowSpan, int colSpan, bool value)
        {
            if (Occupied is null) return;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    Occupied[r, c] = value;
                }
            }
        }

        private async Task<string> GetRandomUnusedPathAsync()
        {
            int tries = GetImageTryCount();
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (!UsedPaths.Contains(p)) return p;
                await Task.Yield();
            }
            return string.Empty;
        }

        private int GetImageTryCount()
        {
            var occ = OccupancyPercent();
            // fewer tries when occupancy is high (we'll clear anyway), more when space is plenty
            if (occ < 40) return 20;
            if (occ < 70) return 16;
            if (occ < 90) return 12;
            return 8;
        }

        // UI handlers
        private void OnDelayInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                DelaySeconds = Math.Max(1u, Math.Min(60u, v));
            }
        }

        private void OnFillInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                FillTargetPercent = Math.Clamp(v, 70, 100);
            }
        }

        private void OnScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MinScale = Math.Clamp(v / 100.0, 0.1, 1.0);
            }
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

        private async Task SaveAndApplyAsync()
        {
            var settings = await SettingsService.LoadAsync();
            settings.DelaySeconds = DelaySeconds;
            settings.TiledFillTargetPercent = FillTargetPercent;
            settings.TiledMinScale = MinScale;
            settings.DirectoryPath = DirectoryPath;
            settings.LastMode = "Tiled";
            settings.TiledCols = TiledCols;
            settings.MinTilePx = MinTilePx;
            // keep panel size fixed; stop persisting size
            await SettingsService.SaveAsync(settings);
            await StartAsync();
        }

        private async Task ChooseAndApplyFolderAsync()
        {
            var directoryPath = await SelectDirectoryAsync();
            if (string.IsNullOrWhiteSpace(directoryPath)) return;
            DirectoryPath = directoryPath;
            ImageService.LoadImages(directoryPath);
            WebViewHost.MapImagesFolder(directoryPath);
            var settings = await SettingsService.LoadAsync();
            settings.DirectoryPath = DirectoryPath;
            await SettingsService.SaveAsync(settings);
            // reset and restart
            RecomputeGrid();
            await StartAsync();
            await InvokeAsync(StateHasChanged);
        }

        private async Task SwitchMode(string mode)
        {
            var settings = await SettingsService.LoadAsync();
            settings.LastMode = mode;
            await SettingsService.SaveAsync(settings);
            if (string.Equals(mode, "Slide", StringComparison.OrdinalIgnoreCase))
                Nav.NavigateTo("/");
        }

        private void ExitApp() => WindowService.Exit();

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
                await StartAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            try { if (_resizeObj is not null) await _resizeObj.InvokeVoidAsync("dispose"); } catch { }
            try { _selfRef?.Dispose(); } catch { }
            ImageService.Dispose();
        }

        [JSInvokable]
        public Task OnPanelSizeChanged(double width, double height)
        {
            // size persistence disabled; accept callback to avoid JS errors
            return Task.CompletedTask;
        }

        private string BuildVirtualHostUrl(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) return string.Empty;
            string rel = Path.GetRelativePath(DirectoryPath, absolutePath).Replace('\\', '/');
            return $"https://{HostName}/{rel}";
        }
    }
}
