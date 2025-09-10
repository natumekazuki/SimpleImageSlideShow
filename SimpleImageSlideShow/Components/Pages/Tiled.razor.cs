using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;
using System.Globalization;
using Windows.Storage.Pickers;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled : IAsyncDisposable
    {
        [Inject]
        public required IImageService ImageService { get; init; }

        [Inject]
        public required ISettingsService SettingsService { get; init; }

        [Inject]
        public required IWindowService WindowService { get; init; }

        [Inject]
        public required IWebViewHostService WebViewHost { get; init; }

        [Inject]
        public required NavigationManager Nav { get; init; }

        [Inject]
        public required IJSRuntime JS { get; init; }

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

        // Clock overlay (reserved area in grid)
        private const double ClockReservedWidth = 320;   // px (keep in sync with CSS)
        private const double ClockReservedHeight = 140;  // px (keep in sync with CSS)
        private const double ClockMarginLeft = 12;       // px
        private const double ClockMarginBottom = 12;     // px
        private bool[,]? ClockCells;
        private bool ClockOverlapped = false;
        private string ClockTime = "--:--";
        private string ClockDate = "--/--(-)";
        private Timer? _clockTimer;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private IJSObjectReference? _resizeObj;
        private DotNetObjectReference<Tiled>? _selfRef;
        private readonly Dictionary<string, string> _dataUrlCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _dataUrlLru = new();
        private const int DataUrlCacheCapacity = 256;

        // Reuse TTL: avoid reusing the same image too soon
        private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
        private readonly PriorityQueue<string, long> _cooldownQueue = new();
        private int ReuseTtlSeconds = 120;

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
            MinScale = Math.Clamp(settings.TiledMinScale, 0.1, 1.0);
            DirectoryPath = settings.DirectoryPath;
            TiledCols = settings.TiledCols > 0 ? settings.TiledCols : 6;
            MinTilePx = settings.MinTilePx > 0 ? settings.MinTilePx : 128;
            ReuseTtlSeconds = settings.TiledReuseTtlSeconds > 0 ? settings.TiledReuseTtlSeconds : 120;
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

        [JSInvokable]
        public async Task OnResize(int w, int h)
        {
            ViewportW = Math.Max(1, w);
            ViewportH = Math.Max(1, h);
            RecomputeGrid();
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
            ComputeClockReservedCells();
            UpdateClockOverlap();
            // reset items on recompute asリセットでOKの仕様
            Items.Clear();
            UsedPaths.Clear();
        }

        private static async Task PrimeInitialAsync()
        {
            // Initial prime disabled to avoid burst loads and duplicates
            await Task.CompletedTask;
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
            var added = await AddOneAsync();
            if (!added)
            {
                await AddWithFifoRemovalAsync();
            }
        }

        // Replace phase now uses FIFO-based removal strategy

        private async Task<bool> AddOneAsync()
        {
            var imagePath = await GetRandomUnusedPathAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return false;
            var entity = await ImageService.LoadImageEntityAsync(imagePath);
            if (entity is null) return false;

            var baseFit = GetBaseFitScale(entity);
            var rand = Random.Shared.NextDouble() * (1.0 - MinScale) + MinScale; // [MinScale,1]
            var scale = baseFit * rand;
            // Strict: only place avoiding clock area in this phase.
            // If it doesn't fit, return false so removal phase can try.
            if (!TryPlaceScaled(entity, baseFit, ref rand, out var item, avoidClock: true))
            {
                return false;
            }

            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(entity.FilePath);
            AddCooldown(entity.FilePath);
            return true;
        }

        // Insert at initially chosen scale, removing oldest tiles (FIFO) until placement is possible.
        private async Task AddWithFifoRemovalAsync()
        {
            // pick a candidate image
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathAsync();
                if (string.IsNullOrWhiteSpace(imagePath)) return;
                var entity = await ImageService.LoadImageEntityAsync(imagePath);
                if (entity is null) continue;

                // choose initial scale once and do NOT degrade it
                var baseFit = GetBaseFitScale(entity);
                var rand = Random.Shared.NextDouble() * (1.0 - MinScale) + MinScale; // [MinScale,1]
                var scale = baseFit * rand;
                var sw = entity.Width * scale;
                var sh = entity.Height * scale;
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                // if cannot fit even on an empty grid, skip this image
                if (reqCols > Cols || reqRows > Rows) continue;

                // try without removal first (avoid clock area)
                if (TryPlace(reqRows, reqCols, out var r0, out var c0, avoidClock: true))
                {
                    var item0 = new TiledItem
                    {
                        Path = entity.FilePath,
                        Row = r0,
                        Col = c0,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Left = OffsetX + c0 * TileW,
                        Top = OffsetY + r0 * TileH,
                        Width = reqCols * TileW,
                        Height = reqRows * TileH,
                        Scale = scale,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(entity.FilePath)
                    };
                    FillCells(item0.Row, item0.Col, item0.RowSpan, item0.ColSpan, true);
                    SetOwners(item0, true);
                    Items.Add(item0);
                    UsedPaths.Add(entity.FilePath);
                    AddCooldown(entity.FilePath);
                    return;
                }

                // simulate FIFO removals on a copy of the occupancy grid to find minimal removals (avoid clock area)
                if (!TryComputeFifoRemovalForPlacement(reqRows, reqCols, out int removeCount, out int rr, out int cc, avoidClock: true))
                {
                    // try again allowing clock area as last resort
                    if (!TryComputeFifoRemovalForPlacement(reqRows, reqCols, out removeCount, out rr, out cc, avoidClock: false))
                    {
                        // couldn't compute (should be rare), try another image
                        continue;
                    }
                }

                // perform a single batch removal animation for the first removeCount items
                if (removeCount > 0)
                {
                    int toRemove = Math.Min(removeCount, Items.Count);
                    for (int i = 0; i < toRemove; i++)
                    {
                        var it = Items[i];
                        it.Removing = true;
                    }
                    StateHasChanged();
                    await Task.Delay(300);

                    for (int i = 0; i < toRemove; i++)
                    {
                        var it = Items[0]; // always oldest
                        FillCells(it.Row, it.Col, it.RowSpan, it.ColSpan, false);
                        SetOwners(it, false);
                        Items.RemoveAt(0);
                        UsedPaths.Remove(it.Path);
                    }
                }

                // place new item at the precomputed location
                var item = new TiledItem
                {
                    Path = entity.FilePath,
                    Row = rr,
                    Col = cc,
                    RowSpan = reqRows,
                    ColSpan = reqCols,
                    Left = OffsetX + cc * TileW,
                    Top = OffsetY + rr * TileH,
                    Width = reqCols * TileW,
                    Height = reqRows * TileH,
                    Scale = scale,
                    ImgWidth = sw,
                    ImgHeight = sh,
                    Src = BuildVirtualHostUrl(entity.FilePath)
                };
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
                SetOwners(item, true);
                Items.Add(item);
                UsedPaths.Add(entity.FilePath);
                AddCooldown(entity.FilePath);
                return;
            }
        }

        private bool TryComputeFifoRemovalForPlacement(int reqRows, int reqCols, out int removeCount, out int row, out int col, bool avoidClock)
        {
            removeCount = 0; row = col = -1;
            if (Occupied is null) return false;

            // copy occupancy
            var occSim = new bool[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    occSim[r, c] = Occupied[r, c];

            // quick success without removals
            if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
            {
                removeCount = 0;
                return true;
            }

            // progressively clear oldest tiles and test
            for (int k = 1; k <= Items.Count; k++)
            {
                var it = Items[k - 1];
                FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
                {
                    removeCount = k;
                    return true;
                }
            }

            return false;
        }

        private bool TryPlaceSim(int rowSpan, int colSpan, bool[,] occ, out int row, out int col, bool avoidClock)
        {
            row = col = -1;
            int rows = occ.GetLength(0);
            int cols = occ.GetLength(1);
            for (int r = 0; r <= rows - rowSpan; r++)
            {
                for (int c = 0; c <= cols - colSpan; c++)
                {
                    bool ok = true;
                    for (int rr = r; rr < r + rowSpan && ok; rr++)
                        for (int cc = c; cc < c + colSpan; cc++)
                            if (occ[rr, cc] || (avoidClock && IsClockCell(rr, cc))) { ok = false; break; }
                    if (ok) { row = r; col = c; return true; }
                }
            }
            return false;
        }

        private static void FillCellsSim(int row, int col, int rowSpan, int colSpan, bool[,] occ, bool value)
        {
            int rows = occ.GetLength(0);
            int cols = occ.GetLength(1);
            for (int r = row; r < row + rowSpan && r < rows; r++)
                for (int c = col; c < col + colSpan && c < cols; c++)
                    occ[r, c] = value;
        }

        private bool TryPlaceScaled(IImageEntity entity, double baseFit, ref double randScale, out TiledItem item, bool avoidClock)
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
                        if (TryPlace(rs, cs, out var r, out var c, avoidClock))
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

                if (TryPlace(reqRows, reqCols, out var rFree, out var cFree, avoidClock: true))
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
            toRemove = [];
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
                        if (IsOverlappingClock(r, c, reqRows, reqCols)) continue;
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
                    if (IsOverlappingClock(r, c, reqRows, reqCols)) continue;
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
            if (Owners is null) return [];
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    var it = Owners[r, c];
                    if (it is not null) set.Add(it);
                }
            }
            return [.. set];
        }

        private bool TryPlace(int rowSpan, int colSpan, out int row, out int col, bool avoidClock)
        {
            row = col = 0;
            if (Occupied is null) return false;
            var candidates = new List<(int r, int c)>();
            for (int r = 0; r <= Rows - rowSpan; r++)
            {
                for (int c = 0; c <= Cols - colSpan; c++)
                {
                    if (CanPlace(r, c, rowSpan, colSpan, avoidClock)) candidates.Add((r, c));
                }
            }
            if (candidates.Count == 0) return false;
            var pick = candidates[Random.Shared.Next(candidates.Count)];
            row = pick.r; col = pick.c; return true;
        }

        private bool CanPlace(int row, int col, int rowSpan, int colSpan, bool avoidClock)
        {
            if (Occupied is null) return false;
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    if (r < 0 || r >= Rows || c < 0 || c >= Cols) return false;
                    if (Occupied[r, c]) return false;
                    if (avoidClock && IsClockCell(r, c)) return false;
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
            UpdateClockOverlap();
        }

        private async Task<string> GetRandomUnusedPathAsync()
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            // Fallback: ignore TTL but avoid duplicates on screen
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

        // Fill target control removed: always aim to fully occupy the grid

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
            // Fill target is implicit (100%); not persisted anymore
            settings.TiledMinScale = MinScale;
            settings.DirectoryPath = DirectoryPath;
            settings.LastMode = "Tiled";
            settings.TiledCols = TiledCols;
            settings.MinTilePx = MinTilePx;
            settings.TiledReuseTtlSeconds = ReuseTtlSeconds;
            // keep panel size fixed; stop persisting size
            await SettingsService.SaveAsync(settings);
            await StartAsync();
        }

        private void AddCooldown(string path)
        {
            try
            {
                var until = DateTime.UtcNow.AddSeconds(Math.Max(1, ReuseTtlSeconds));
                _cooldown[path] = until;
                _cooldownQueue.Enqueue(path, until.Ticks);
            }
            catch { }
        }

        private void CleanupCooldown()
        {
            try
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                while (_cooldownQueue.TryPeek(out var path, out var ticks) && ticks <= nowTicks)
                {
                    _cooldownQueue.Dequeue();
                    if (_cooldown.TryGetValue(path, out var dt) && dt.Ticks <= nowTicks)
                    {
                        _cooldown.Remove(path);
                    }
                }
            }
            catch { }
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

                // 初回はディレイ無視で1枚挿入（グリッド初期化を待つ）
                await WaitForGridReadyAsync(TimeSpan.FromMilliseconds(800));
                try
                {
                    await StepAsync();
                    StateHasChanged();
                }
                catch { }

                await StartAsync();
            }
        }

        private async Task WaitForGridReadyAsync(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((Occupied is null || Cols <= 0 || Rows <= 0) && sw.Elapsed < timeout)
            {
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            try { if (_resizeObj is not null) await _resizeObj.InvokeVoidAsync("dispose"); } catch { }
            try { _selfRef?.Dispose(); } catch { }
            try { _clockTimer?.Dispose(); } catch { }
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

        private void UpdateClockText()
        {
            var now = DateTime.Now;
            ClockTime = now.ToString("HH:mm");
            var ja = CultureInfo.GetCultureInfo("ja-JP");
            var dow = now.ToString("ddd", ja);
            ClockDate = $"{now:MM/dd}({dow})";
        }

        private void ComputeClockReservedCells()
        {
            if (Rows <= 0 || Cols <= 0)
            {
                ClockCells = null; return;
            }
            ClockCells = new bool[Rows, Cols];

            // Clock rectangle in viewport px
            double cx1 = ClockMarginLeft;
            double cy2 = ViewportH - ClockMarginBottom;
            double cx2 = cx1 + ClockReservedWidth;
            double cy1 = cy2 - ClockReservedHeight;

            // Grid rectangle
            double gx1 = OffsetX;
            double gy1 = OffsetY;
            double gx2 = OffsetX + GridW;
            double gy2 = OffsetY + GridH;

            // Intersection
            double ix1 = Math.Max(cx1, gx1);
            double iy1 = Math.Max(cy1, gy1);
            double ix2 = Math.Min(cx2, gx2);
            double iy2 = Math.Min(cy2, gy2);
            if (ix2 <= ix1 || iy2 <= iy1) return; // no overlap

            int cStart = Math.Clamp((int)Math.Floor((ix1 - gx1) / Math.Max(1.0, TileW)), 0, Cols);
            int cEndEx = Math.Clamp((int)Math.Ceiling((ix2 - gx1) / Math.Max(1.0, TileW)), 0, Cols);
            int rStart = Math.Clamp((int)Math.Floor((iy1 - gy1) / Math.Max(1.0, TileH)), 0, Rows);
            int rEndEx = Math.Clamp((int)Math.Ceiling((iy2 - gy1) / Math.Max(1.0, TileH)), 0, Rows);

            for (int r = rStart; r < rEndEx; r++)
                for (int c = cStart; c < cEndEx; c++)
                    ClockCells[r, c] = true;
        }

        private bool IsClockCell(int r, int c)
            => ClockCells is not null && r >= 0 && r < Rows && c >= 0 && c < Cols && ClockCells[r, c];

        private bool IsOverlappingClock(int row, int col, int rowSpan, int colSpan)
        {
            if (ClockCells is null) return false;
            for (int r = row; r < row + rowSpan; r++)
                for (int c = col; c < col + colSpan; c++)
                    if (IsClockCell(r, c)) return true;
            return false;
        }

        private void UpdateClockOverlap()
        {
            if (Occupied is null || ClockCells is null) { ClockOverlapped = false; return; }
            bool any = false;
            for (int r = 0; r < Rows && !any; r++)
                for (int c = 0; c < Cols && !any; c++)
                    if (ClockCells[r, c] && Occupied[r, c]) any = true;
            ClockOverlapped = any;
        }
    }
}
