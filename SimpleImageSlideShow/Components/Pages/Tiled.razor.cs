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
            public string? AudioSrc { get; init; }
        }

        private readonly record struct ViewportSize(double Width, double Height);

        private List<TiledItem> Items { get; set; } = [];
        private HashSet<string> UsedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        private uint DelaySeconds { get; set; } = 5;
        private double AudioVolumePercent { get; set; } = 0;
        private double AudioVolume => Math.Clamp(AudioVolumePercent, 0, 100) / 100.0;
        private const double AudioSilenceEpsilon = 0.0001;
        private double MinScale { get; set; } = 0.5;
        private double MaxScale { get; set; } = 1.0;
        private string BackgroundColor { get; set; } = DefaultBackgroundColor;
        private string? DirectoryPath { get; set; }
        private bool IsFullScreen { get; set; }
        private bool IsWindowModeChanging { get; set; }
        private string HostName => WebViewHost.HostName;
        private int MinTilePx = 128; // 最小タイル幅（px）
        private int ColsMax => (int)Math.Max(1, Math.Floor(ViewportW / Math.Max(1, MinTilePx)));
        // Panel size is fixed in CSS for tiled mode; no persistence

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
        private const double ClockBaseWidth = 320;   // px (keep in sync with CSS)
        private const double ClockBaseHeight = 140;  // px (keep in sync with CSS)
        private const double ClockMarginHorizontal = 12; // px
        private const double ClockMarginVertical = 12;   // px
        private const string ClockCornerTopLeft = "TopLeft";
        private const string ClockCornerTopRight = "TopRight";
        private const string ClockCornerTopCenter = "TopCenter";
        private const string ClockCornerBottomLeft = "BottomLeft";
        private const string ClockCornerBottomRight = "BottomRight";
        private const string ClockCornerBottomCenter = "BottomCenter";
        private const string ClockCornerCenter = "Center";
        private static readonly (string Value, string Label)[] ClockCornerChoices = new[]
        {
            (ClockCornerTopLeft, "Top Left"),
            (ClockCornerTopRight, "Top Right"),
            (ClockCornerTopCenter, "Top Center"),
            (ClockCornerBottomLeft, "Bottom Left"),
            (ClockCornerBottomRight, "Bottom Right"),
            (ClockCornerBottomCenter, "Bottom Center"),
            (ClockCornerCenter, "Center")
        };
        private bool ShowClock { get; set; } = true;
        private bool AvoidClockOverlap { get; set; } = true;
        private string ClockCorner { get; set; } = ClockCornerBottomLeft;
        private double ClockScale { get; set; } = 1.0;
        private bool[,]? ClockCells;
        private bool ClockOverlapped = false;
        private string ClockTime = "--:--";
        private string ClockDate = "--/--(-)";
        private Timer? _clockTimer;
        private CancellationTokenSource? _clockLayoutUpdateCts;
        private static readonly TimeSpan ClockLayoutDebounceDelay = TimeSpan.FromMilliseconds(150);

        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private IJSObjectReference? _resizeObj;
        private DotNetObjectReference<Tiled>? _selfRef;
        private const int PlanCapacity = 5; // plan up to 5 steps ahead
        private uint RandomScaleTries { get; set; } = 10; // random ratio attempts per placement
        private const double ShrinkGuardThreshold = 0.25; // 原寸未満回避を適用する長辺比率の上限
        private const double PositionJitterRatio = 0.22; // タイル枠内で遊ばせる割合（格子を細かくせずランダム化）
        private const double PositionJitterMaxPx = 64;

        // Precomputed next step to reduce stutter on tick
        private record PlannedStep
        {
            public required string Path { get; init; }
            public required int Row { get; init; }
            public required int Col { get; init; }
            public required int RowSpan { get; init; }
            public required int ColSpan { get; init; }
            public required double Scale { get; init; }
            public required double ImgWidth { get; init; }
            public required double ImgHeight { get; init; }
            public required string Src { get; init; }
            public string? AudioSrc { get; init; }
            public int RemoveCount { get; init; }
        }

        private readonly List<PlannedStep> _planQueue = new();
        private TiledItem? _lastTickItem;

        // Reuse TTL: avoid reusing the same image too soon
        private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
        private readonly PriorityQueue<string, long> _cooldownQueue = new();
        private int ReuseTtlSeconds = 120;
        private const string DefaultBackgroundColor = "#D3D3D3";

        private string BackgroundStyle => $"--app-background-color:{BackgroundColor};background-color:var(--app-background-color);";

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
            await WindowService.InitializeAsync();
            UpdateWindowMode(WindowService.CurrentMode, force: true);
            WindowService.ModeChanged += OnWindowModeChanged;

            var settings = await SettingsService.LoadAsync();
            DelaySeconds = settings.DelaySeconds > 0 ? settings.DelaySeconds : 5;
            AudioVolumePercent = Math.Clamp(settings.AudioVolumePercent, 0, 100);
            MinScale = Math.Clamp(settings.TiledMinScale, 0.1, 1.0);
            MaxScale = Math.Clamp(settings.TiledMaxScale, 0.1, 1.0);
            if (MaxScale < MinScale) MaxScale = MinScale;
            DirectoryPath = settings.DirectoryPath;
            BackgroundColor = NormalizeBackgroundColor(settings.BackgroundColor);
            TiledCols = settings.TiledCols > 0 ? settings.TiledCols : 6;
            MinTilePx = settings.MinTilePx > 0 ? settings.MinTilePx : 128;
            ReuseTtlSeconds = settings.TiledReuseTtlSeconds > 0 ? settings.TiledReuseTtlSeconds : 120;
            RandomScaleTries = settings.RandomScaleTries > 0 ? settings.RandomScaleTries : 10;
            ShowClock = settings.ShowTiledClock;
            AvoidClockOverlap = settings.AvoidTiledClockOverlap;
            ClockCorner = NormalizeClockCorner(settings.TiledClockCorner);
            ClockScale = Math.Clamp(settings.TiledClockScale, 0.5, 2.0);

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

        private async Task StartAsync()
        {
            await StopAsync();
            _cts = new CancellationTokenSource();
            _lastTickItem = Items.LastOrDefault();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            var shouldWait = _lastTickItem is not null;
            while (!token.IsCancellationRequested)
            {
                var waitTarget = _lastTickItem;
                if (waitTarget is not null || shouldWait)
                {
                    try
                    {
                        await WaitForNextTickAsync(waitTarget, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                shouldWait = true;

                TiledItem? newItem = null;
                try
                {
                    await InvokeAsync(async () =>
                    {
                        var item = await ApplyPlannedOrStepAsync();
                        StateHasChanged();
                        newItem = item;
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }

                _lastTickItem = newItem ?? Items.LastOrDefault();
            }
        }

        private async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();
                if (_loopTask is not null)
                    await Task.WhenAny(_loopTask, Task.Delay(500));
                await StopAudioPlaybackAsync();
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

        private async Task<TiledItem?> StepAsync()
        {
            if (Occupied is null) return null;
            var added = await AddOneAsync();
            if (added is null)
            {
                added = await AddWithFifoRemovalAsync();
            }
            try { await EnsurePlanAsync(); } catch { }
            return added;
        }

        private async Task<TiledItem?> AddOneAsync()
        {
            var imagePath = await GetRandomUnusedPathAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var size = await ImageService.GetImageSizeAsync(imagePath);
            if (size is null) return null;
            var (origW, origH) = size.Value;

            // 画面長辺比ベースでサイズを決定（アップスケール禁止）。
            var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
            var lo = Math.Min(MinScale, hi);
            var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
            var iLong = Math.Max(origW, origH);
            var rImg = vLong > 0 ? (iLong / vLong) : MinScale; // 原寸の長辺が画面長辺に占める比率
            // B: 小さい画像は原寸未満にしない → rImg が範囲内なら下限を rImg まで引き上げ
            if (rImg <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg);
            var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
            if (!TryPlaceLongEdgeBasedNoUpscale(origW, origH, imagePath, lo, hi, rand, out var item, avoidClock: ShowClock && AvoidClockOverlap))
            {
                return null;
            }

            item = item with { AudioSrc = GetAudioUrlForImage(imagePath) };
            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(imagePath);
            AddCooldown(imagePath);
            return item;
        }

        // Insert at initially chosen scale, removing oldest tiles (FIFO) until placement is possible.
        private async Task<TiledItem?> AddWithFifoRemovalAsync()
        {
            // pick a candidate image
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathAsync();
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                if (size is null) continue;
                var (origW, origH) = size.Value;

                // 長辺比ベースの初期候補（アップスケール禁止）
                var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
                var lo = Math.Min(MinScale, hi);
                var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
                var iLong = Math.Max(origW, origH);
                var rImg = vLong > 0 ? (iLong / vLong) : MinScale;
                if (rImg <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));

                // if cannot fit even on an empty grid, skip this image
                if (reqCols > Cols || reqRows > Rows) continue;

                // try without removal first (avoid clock area) with multiple random scales
                // attempt initial chosen scale first
                var avoidClock = ShowClock && AvoidClockOverlap;
                if (TryPlace(reqRows, reqCols, out var r0, out var c0, avoidClock: avoidClock))
                {
                    var src0 = BuildVirtualHostUrl(imagePath);
                    var audio0 = GetAudioUrlForImage(imagePath);
                    var item0 = CreateTiledItem(imagePath, r0, c0, reqRows, reqCols, rand, sw, sh, src0, audio0);
                    FillCells(item0.Row, item0.Col, item0.RowSpan, item0.ColSpan, true);
                    SetOwners(item0, true);
                    Items.Add(item0);
                    UsedPaths.Add(imagePath);
                    AddCooldown(imagePath);
                    return item0;
                }

                // then try a few random scales
                for (int tries = 0; tries < RandomScaleTries; tries++)
                {
                    var rtry = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                    var (swD, shD) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rtry, clampToGrid: true);
                    int reqColsD = Math.Max(1, (int)Math.Ceiling(swD / TileW));
                    int reqRowsD = Math.Max(1, (int)Math.Ceiling(shD / TileH));
                    if (reqColsD <= Cols && reqRowsD <= Rows && TryPlace(reqRowsD, reqColsD, out var rD, out var cD, avoidClock: avoidClock))
                    {
                        var srcD = BuildVirtualHostUrl(imagePath);
                        var audioD = GetAudioUrlForImage(imagePath);
                        var itemD = CreateTiledItem(imagePath, rD, cD, reqRowsD, reqColsD, rtry, swD, shD, srcD, audioD);
                        FillCells(itemD.Row, itemD.Col, itemD.RowSpan, itemD.ColSpan, true);
                        SetOwners(itemD, true);
                        Items.Add(itemD);
                        UsedPaths.Add(imagePath);
                        AddCooldown(imagePath);
                        return itemD;
                    }
                }

                // simulate FIFO removals on a copy of the occupancy grid to find minimal removals (avoid clock area)
                if (!TryComputeFifoRemovalForPlacement(reqRows, reqCols, out int removeCount, out int rr, out int cc, avoidClock: avoidClock))
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
                var src = BuildVirtualHostUrl(imagePath);
                var audio = GetAudioUrlForImage(imagePath);
                var item = CreateTiledItem(imagePath, rr, cc, reqRows, reqCols, rand, sw, sh, src, audio);
                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
                SetOwners(item, true);
                Items.Add(item);
                UsedPaths.Add(imagePath);
                AddCooldown(imagePath);
                return item;
            }
            return null;
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

            // Build all candidate top-left positions and shuffle for random probing order
            int maxR = Math.Max(0, rows - rowSpan + 1);
            int maxC = Math.Max(0, cols - colSpan + 1);
            if (maxR == 0 || maxC == 0) return false;

            var candidates = new List<(int r, int c)>(maxR * maxC);
            for (int r = 0; r < maxR; r++)
                for (int c = 0; c < maxC; c++)
                    candidates.Add((r, c));

            // Fisher–Yates shuffle
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            foreach (var (r, c) in candidates)
            {
                bool ok = true;
                for (int rr = r; rr < r + rowSpan && ok; rr++)
                {
                    for (int cc = c; cc < c + colSpan; cc++)
                    {
                        if (occ[rr, cc] || (avoidClock && IsClockCell(rr, cc))) { ok = false; break; }
                    }
                }
                if (ok) { row = r; col = c; return true; }
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

        private (double sw, double sh) ComputeViewportAreaTargetNoUpscale(double origW, double origH, double ratio, bool clampToGrid)
        {
            var a = origH > 0 ? (origW / origH) : 1.0;
            var screenArea = Math.Max(1.0, ViewportW) * Math.Max(1.0, ViewportH);
            var targetArea = Math.Max(0.0, ratio) * screenArea;
            var desW = Math.Sqrt(targetArea * a);
            var desH = Math.Sqrt(targetArea / Math.Max(1e-9, a));
            // 先にアップスケール禁止（原寸上限）を適用
            var sw = Math.Min(desW, origW);
            var sh = Math.Min(desH, origH);
            // グリッドに収まるようアスペクト比を保って一様スケール
            if (clampToGrid)
            {
                var s = Math.Min(GridW / Math.Max(1.0, sw), GridH / Math.Max(1.0, sh));
                s = Math.Min(1.0, s);
                sw *= s; sh *= s;
            }
            if (!double.IsFinite(sw) || sw <= 0) sw = Math.Min(origW, GridW);
            if (!double.IsFinite(sh) || sh <= 0) sh = Math.Min(origH, GridH);
            return (sw, sh);
        }

        private (double sw, double sh) ComputeViewportLongEdgeTargetNoUpscale(double origW, double origH, double ratio, bool clampToGrid)
        {
            var a = origH > 0 ? (origW / origH) : 1.0;
            var vLong = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
            var desW = 0.0; var desH = 0.0;
            if (a >= 1.0)
            {
                desW = Math.Max(0.0, ratio) * vLong;
                desH = desW / Math.Max(1e-9, a);
            }
            else
            {
                desH = Math.Max(0.0, ratio) * vLong;
                desW = desH * a;
            }
            // 先にアップスケール禁止（原寸上限）
            var sw = Math.Min(desW, origW);
            var sh = Math.Min(desH, origH);
            // グリッドに収める（等倍スケール）
            if (clampToGrid)
            {
                var s = Math.Min(GridW / Math.Max(1.0, sw), GridH / Math.Max(1.0, sh));
                s = Math.Min(1.0, s);
                sw *= s; sh *= s;
            }
            if (!double.IsFinite(sw) || sw <= 0) sw = Math.Min(origW, GridW);
            if (!double.IsFinite(sh) || sh <= 0) sh = Math.Min(origH, GridH);
            return (sw, sh);
        }

        private (double left, double top, double width, double height) ComputeJitteredFrame(int row, int col, int rowSpan, int colSpan)
        {
            var areaW = colSpan * TileW;
            var areaH = rowSpan * TileH;
            var slackX = Math.Min(PositionJitterMaxPx, areaW * PositionJitterRatio);
            var slackY = Math.Min(PositionJitterMaxPx, areaH * PositionJitterRatio);
            var width = Math.Max(1.0, areaW - slackX);
            var height = Math.Max(1.0, areaH - slackY);
            var jitterX = slackX > 0 ? Random.Shared.NextDouble() * slackX : 0;
            var jitterY = slackY > 0 ? Random.Shared.NextDouble() * slackY : 0;
            var left = OffsetX + col * TileW + jitterX;
            var top = OffsetY + row * TileH + jitterY;
            return (left, top, width, height);
        }

        private TiledItem CreateTiledItem(string path, int row, int col, int rowSpan, int colSpan, double scale, double imgWidth, double imgHeight, string src, string? audioSrc = null)
        {
            var (left, top, width, height) = ComputeJitteredFrame(row, col, rowSpan, colSpan);
            return new TiledItem
            {
                Path = path,
                Row = row,
                Col = col,
                RowSpan = rowSpan,
                ColSpan = colSpan,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Scale = scale,
                ImgWidth = imgWidth,
                ImgHeight = imgHeight,
                Src = src,
                AudioSrc = audioSrc
            };
        }

        private bool TryPlaceAreaBasedNoUpscale(double origW, double origH, string filePath, double lo, double hi, double initialRatio, out TiledItem item, bool avoidClock)
        {
            item = default!;
            for (int attempt = 0; attempt < RandomScaleTries; attempt++)
            {
                var ratio = attempt == 0 ? initialRatio : (lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo);
                ratio = Math.Clamp(ratio, lo, hi);
                var (sw, sh) = ComputeViewportAreaTargetNoUpscale(origW, origH, ratio, clampToGrid: true);

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
                            var src = BuildVirtualHostUrl(filePath);
                            item = CreateTiledItem(filePath, r, c, rs, cs, ratio, sw, sh, src);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private bool TryPlaceLongEdgeBasedNoUpscale(double origW, double origH, string filePath, double lo, double hi, double initialRatio, out TiledItem item, bool avoidClock)
        {
            item = default!;
            for (int attempt = 0; attempt < RandomScaleTries; attempt++)
            {
                var ratio = attempt == 0 ? initialRatio : (lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo);
                ratio = Math.Clamp(ratio, lo, hi);
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, ratio, clampToGrid: true);

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
                            var src = BuildVirtualHostUrl(filePath);
                            item = CreateTiledItem(filePath, r, c, rs, cs, ratio, sw, sh, src);
                            return true;
                        }
                    }
                }
            }
            return false;
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

        private double GetBaseFitScaleFromDims(double width, double height)
        {
            // If original is larger than grid area, scale down to fit within grid, else 1.0
            var sx = GridW / Math.Max(1.0, width);
            var sy = GridH / Math.Max(1.0, height);
            var fit = Math.Min(1.0, Math.Min(sx, sy));
            return double.IsFinite(fit) && fit > 0 ? fit : 1.0;
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

        private void RemoveClockOverlaps()
        {
            if (!AvoidClockOverlap || !ShowClock || ClockCells is null || Occupied is null) return;

            var removedAny = false;
            foreach (var item in Items.ToList())
            {
                if (!IsOverlappingClock(item.Row, item.Col, item.RowSpan, item.ColSpan)) continue;

                FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, false);
                SetOwners(item, false);
                Items.Remove(item);
                UsedPaths.Remove(item.Path);
                removedAny = true;
            }

            if (removedAny)
            {
                UpdateClockOverlap();
                InvalidatePlan();
            }
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

        private void OnMinScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MinScale = Math.Clamp(v / 100.0, 0.1, 1.0);
                if (MaxScale < MinScale) MaxScale = MinScale;
            }
        }

        private void OnMaxScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && int.TryParse(s, out var v))
            {
                MaxScale = Math.Clamp(v / 100.0, 0.1, 1.0);
                if (MaxScale < MinScale) MinScale = MaxScale;
            }
        }

        private void OnBackgroundColorSelected(string value)
        {
            var next = NormalizeBackgroundColor(value);
            if (!string.Equals(next, BackgroundColor, StringComparison.OrdinalIgnoreCase))
            {
                BackgroundColor = next;
            }
        }

        private static string NormalizeBackgroundColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultBackgroundColor;
            var trimmed = value.Trim();
            return trimmed.StartsWith('#') ? trimmed : $"#{trimmed}";
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

        private void OnRandomScaleTriesInput(ChangeEventArgs e)
        {
            if (e.Value is string s && uint.TryParse(s, out var v))
            {
                RandomScaleTries = Math.Max(1u, Math.Min(500u, v));
            }
        }

        private void OnClockToggleChanged(ChangeEventArgs e)
        {
            var show = ShowClock;
            if (e.Value is bool b)
            {
                show = b;
            }
            else if (e.Value is string s && bool.TryParse(s, out var parsed))
            {
                show = parsed;
            }

            if (ShowClock == show) return;
            ShowClock = show;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockAvoidOverlapChanged(ChangeEventArgs e)
        {
            var avoid = AvoidClockOverlap;
            if (e.Value is bool b)
            {
                avoid = b;
            }
            else if (e.Value is string s && bool.TryParse(s, out var parsed))
            {
                avoid = parsed;
            }

            if (AvoidClockOverlap == avoid) return;
            AvoidClockOverlap = avoid;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockCornerChanged(ChangeEventArgs e)
        {
            var next = NormalizeClockCorner(e.Value?.ToString());
            if (string.Equals(next, ClockCorner, StringComparison.Ordinal)) return;
            ClockCorner = next;
            RefreshClockLayout(immediate: true);
        }

        private void OnClockScaleInput(ChangeEventArgs e)
        {
            if (e.Value is string s && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                var nextScale = Math.Clamp(v / 100.0, 0.5, 5.0);
                if (Math.Abs(nextScale - ClockScale) < 0.0001) return;
                ClockScale = nextScale;
                RefreshClockLayout(immediate: false);
            }
        }

        private void RefreshClockLayout(bool immediate)
        {
            if (immediate)
            {
                CancelClockLayoutUpdate();
                ComputeClockReservedCells();
                UpdateClockOverlap();
                RemoveClockOverlaps();
                InvalidatePlan();
                StateHasChanged();
            }
            else
            {
                StateHasChanged();
                ScheduleClockLayoutUpdate();
            }
        }

        private void ScheduleClockLayoutUpdate()
        {
            var previous = _clockLayoutUpdateCts;
            var nextCts = new CancellationTokenSource();
            _clockLayoutUpdateCts = nextCts;

            if (previous is not null)
            {
                try { previous.Cancel(); } catch { }
                previous.Dispose();
            }

            _ = DebouncedClockLayoutUpdateAsync(nextCts);
        }

        private async Task DebouncedClockLayoutUpdateAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(ClockLayoutDebounceDelay, cts.Token);
                if (cts.IsCancellationRequested) return;
                await InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    ComputeClockReservedCells();
                    UpdateClockOverlap();
                    RemoveClockOverlaps();
                    InvalidatePlan();
                    StateHasChanged();
                });
            }
            catch (TaskCanceledException) { }
            catch { }
            finally
            {
                if (_clockLayoutUpdateCts == cts)
                {
                    _clockLayoutUpdateCts = null;
                }
                cts.Dispose();
            }
        }

        private void CancelClockLayoutUpdate()
        {
            var cts = _clockLayoutUpdateCts;
            if (cts is null) return;
            _clockLayoutUpdateCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private async Task OnVolumeInput(ChangeEventArgs e)
        {
            if (e.Value is string s && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                AudioVolumePercent = Math.Clamp(v, 0, 100);
                await ApplyAudioVolumeToJsAsync();
            }
        }

        private async Task SaveAndApplyAsync()
        {
            var settings = await SettingsService.LoadAsync();
            settings.DelaySeconds = DelaySeconds;
            // Fill target is implicit (100%); not persisted anymore
            settings.TiledMinScale = MinScale;
            settings.TiledMaxScale = MaxScale;
            settings.DirectoryPath = DirectoryPath;
            settings.LastMode = "Tiled";
            settings.BackgroundColor = BackgroundColor;
            settings.TiledCols = TiledCols;
            settings.MinTilePx = MinTilePx;
            settings.TiledReuseTtlSeconds = ReuseTtlSeconds;
            settings.RandomScaleTries = RandomScaleTries;
            settings.AudioVolumePercent = AudioVolumePercent;
            settings.ShowTiledClock = ShowClock;
            settings.AvoidTiledClockOverlap = AvoidClockOverlap;
            settings.TiledClockCorner = ClockCorner;
            settings.TiledClockScale = ClockScale;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            // keep panel size fixed; stop persisting size
            await SettingsService.SaveAsync(settings);
            await StartAsync();
            try { await EnsurePlanAsync(); } catch { }
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

            await StopAsync();

            DirectoryPath = directoryPath;
            ImageService.LoadImages(directoryPath);
            WebViewHost.MapImagesFolder(directoryPath);
            var settings = await SettingsService.LoadAsync();
            settings.DirectoryPath = DirectoryPath;
            settings.BackgroundColor = BackgroundColor;
            settings.WindowDisplayMode = IsFullScreen ? "FullScreen" : "Windowed";
            await SettingsService.SaveAsync(settings);

            Nav.NavigateTo(Nav.Uri, forceLoad: true);
        }

        private void ResetImageState()
        {
            Items.Clear();
            UsedPaths.Clear();
            _planQueue.Clear();
            _lastTickItem = null;

            _cooldown.Clear();
            while (_cooldownQueue.TryDequeue(out _, out _)) { }

            Occupied = null;
            Owners = null;
        }

        private async Task SwitchMode(string mode)
        {
            var settings = await SettingsService.LoadAsync();
            settings.LastMode = mode;
            await SettingsService.SaveAsync(settings);
            if (string.Equals(mode, "Slide", StringComparison.OrdinalIgnoreCase))
                Nav.NavigateTo("/");
        }

        private async Task ToggleWindowModeAsync()
        {
            if (IsWindowModeChanging) return;
            IsWindowModeChanging = true;
            try
            {
                await WindowService.ToggleModeAsync();
            }
            catch
            {
                // swallow; UI will refresh via event if successful
            }
            finally
            {
                IsWindowModeChanging = false;
                await RefreshViewportAsync();
                await InvokeAsync(StateHasChanged);
            }
        }

        private async void OnWindowModeChanged(object? sender, WindowDisplayModeChangedEventArgs e)
        {
            UpdateWindowMode(e.Mode);
            await RefreshViewportAsync();
        }

        private void UpdateWindowMode(WindowDisplayMode mode, bool force = false)
        {
            var isFull = mode == WindowDisplayMode.FullScreen;
            if (!force && isFull == IsFullScreen) return;
            IsFullScreen = isFull;
            if (!force)
            {
                _ = InvokeAsync(StateHasChanged);
            }
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

                await ApplyAudioVolumeToJsAsync();

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

        private void InvalidatePlan() => _planQueue.Clear();

        private async Task<TiledItem?> ApplyPlannedOrStepAsync()
        {
            if (_planQueue.Count == 0)
            {
                return await StepAsync();
            }

            var plan = _planQueue[0];
            _planQueue.RemoveAt(0);

            // Apply removals with fade-out animation
            int toRemove = Math.Min(plan.RemoveCount, Items.Count);
            if (toRemove > 0)
            {
                for (int i = 0; i < toRemove; i++) Items[i].Removing = true;
                StateHasChanged();
                await Task.Delay(300);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = Items[0];
                    FillCells(it.Row, it.Col, it.RowSpan, it.ColSpan, false);
                    SetOwners(it, false);
                    Items.RemoveAt(0);
                    UsedPaths.Remove(it.Path);
                }
            }

            var item = CreateTiledItem(plan.Path, plan.Row, plan.Col, plan.RowSpan, plan.ColSpan, plan.Scale, plan.ImgWidth, plan.ImgHeight, plan.Src, plan.AudioSrc);
            FillCells(item.Row, item.Col, item.RowSpan, item.ColSpan, true);
            SetOwners(item, true);
            Items.Add(item);
            UsedPaths.Add(plan.Path);
            AddCooldown(plan.Path);

            try { await EnsurePlanAsync(); } catch { }
            return item;
        }

        private record SimItem(string Path, int Row, int Col, int RowSpan, int ColSpan);

        private async Task EnsurePlanAsync()
        {
            if (Occupied is null) { _planQueue.Clear(); return; }
            // Fill up to capacity
            int need = PlanCapacity - _planQueue.Count;
            if (need <= 0) return;

            // Simulation state based on current real state plus existing plan
            var occSim = (bool[,])Occupied.Clone();
            var simItems = new List<SimItem>(Items.Select(it => new SimItem(it.Path, it.Row, it.Col, it.RowSpan, it.ColSpan)));
            var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ps in _planQueue)
            {
                // Apply already planned steps to simulation so we stack further plans correctly
                // Remove FIFO items as indicated
                int toRemove = Math.Min(ps.RemoveCount, simItems.Count);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = simItems[0];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems.RemoveAt(0);
                }
                // Add planned item
                FillCellsSim(ps.Row, ps.Col, ps.RowSpan, ps.ColSpan, occSim, true);
                simItems.Add(new SimItem(ps.Path, ps.Row, ps.Col, ps.RowSpan, ps.ColSpan));
                plannedPaths.Add(ps.Path);
            }

            // Build a used-set for planning (current + screen + planned)
            var usedForPlan = new HashSet<string>(UsedPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var it in simItems) usedForPlan.Add(it.Path);
            foreach (var p in plannedPaths) usedForPlan.Add(p);

            for (int n = 0; n < need; n++)
            {
                var plan = await ComputeOnePlanAsync(occSim, simItems, usedForPlan);
                if (plan is null) break;
                _planQueue.Add(plan);
                // Apply to sim
                int toRemove = Math.Min(plan.RemoveCount, simItems.Count);
                for (int i = 0; i < toRemove; i++)
                {
                    var it = simItems[0];
                    FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                    simItems.RemoveAt(0);
                }
                FillCellsSim(plan.Row, plan.Col, plan.RowSpan, plan.ColSpan, occSim, true);
                simItems.Add(new SimItem(plan.Path, plan.Row, plan.Col, plan.RowSpan, plan.ColSpan));
                usedForPlan.Add(plan.Path);
                try { await PreloadImageUrlAsync(plan.Src); } catch { }
            }
        }

        private async Task<PlannedStep?> ComputeOnePlanAsync(bool[,] occSim, List<SimItem> simItems, HashSet<string> usedForPlan)
        {
            const int imageTries = 40;
            for (int t = 0; t < imageTries; t++)
            {
                var imagePath = await GetRandomUnusedPathForPlanAsync(usedForPlan);
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                var size = await ImageService.GetImageSizeAsync(imagePath);
                if (size is null) continue;
                var (origW, origH) = size.Value;

                var hi = Math.Max(MinScale, Math.Min(1.0, MaxScale));
                var lo = Math.Min(MinScale, hi);
                var vLong2 = Math.Max(Math.Max(1.0, ViewportW), Math.Max(1.0, ViewportH));
                var iLong2 = Math.Max(origW, origH);
                var rImg2 = vLong2 > 0 ? (iLong2 / vLong2) : MinScale;
                if (rImg2 <= ShrinkGuardThreshold) lo = Math.Max(lo, rImg2);
                var rand = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                var (sw, sh) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rand, clampToGrid: true);
                int reqCols = Math.Max(1, (int)Math.Ceiling(sw / TileW));
                int reqRows = Math.Max(1, (int)Math.Ceiling(sh / TileH));
                if (reqCols > Cols || reqRows > Rows) continue;

                var avoidClock = ShowClock && AvoidClockOverlap;
                if (TryPlaceSim(reqRows, reqCols, occSim, out var r0, out var c0, avoidClock: avoidClock))
                {
                    return new PlannedStep
                    {
                        Path = imagePath,
                        Row = r0,
                        Col = c0,
                        RowSpan = reqRows,
                        ColSpan = reqCols,
                        Scale = rand,
                        ImgWidth = sw,
                        ImgHeight = sh,
                        Src = BuildVirtualHostUrl(imagePath),
                        AudioSrc = GetAudioUrlForImage(imagePath),
                        RemoveCount = 0
                    };
                }

                // Try additional random scales for no-removal placement
                for (int tries = 0; tries < RandomScaleTries; tries++)
                {
                    var rtry = lo < hi ? lo + Random.Shared.NextDouble() * (hi - lo) : lo;
                    var (swD, shD) = ComputeViewportLongEdgeTargetNoUpscale(origW, origH, rtry, clampToGrid: true);
                    int reqColsD = Math.Max(1, (int)Math.Ceiling(swD / TileW));
                    int reqRowsD = Math.Max(1, (int)Math.Ceiling(shD / TileH));
                    if (reqColsD <= Cols && reqRowsD <= Rows && TryPlaceSim(reqRowsD, reqColsD, occSim, out var rD, out var cD, avoidClock: avoidClock))
                    {
                        return new PlannedStep
                        {
                            Path = imagePath,
                            Row = rD,
                            Col = cD,
                            RowSpan = reqRowsD,
                            ColSpan = reqColsD,
                            Scale = rtry,
                            ImgWidth = swD,
                            ImgHeight = shD,
                            Src = BuildVirtualHostUrl(imagePath),
                            AudioSrc = GetAudioUrlForImage(imagePath),
                            RemoveCount = 0
                        };
                    }
                }

                if (!TryComputeFifoRemovalForPlacementSim(reqRows, reqCols, occSim, simItems, out int removeCount, out int rr, out int cc, avoidClock: avoidClock))
                {
                    if (!TryComputeFifoRemovalForPlacementSim(reqRows, reqCols, occSim, simItems, out removeCount, out rr, out cc, avoidClock: false))
                    {
                        continue;
                    }
                }

                return new PlannedStep
                {
                    Path = imagePath,
                    Row = rr,
                    Col = cc,
                    RowSpan = reqRows,
                    ColSpan = reqCols,
                    Scale = rand,
                    ImgWidth = sw,
                    ImgHeight = sh,
                    Src = BuildVirtualHostUrl(imagePath),
                    AudioSrc = GetAudioUrlForImage(imagePath),
                    RemoveCount = Math.Max(0, removeCount)
                };
            }
            return null;
        }

        private bool TryComputeFifoRemovalForPlacementSim(int reqRows, int reqCols, bool[,] occ, List<SimItem> simItems, out int removeCount, out int row, out int col, bool avoidClock)
        {
            removeCount = 0; row = col = -1;
            int rows = occ.GetLength(0), cols = occ.GetLength(1);
            // Quick success without removals
            if (TryPlaceSim(reqRows, reqCols, occ, out row, out col, avoidClock))
            {
                removeCount = 0;
                return true;
            }
            // Work on a copy
            var occSim = new bool[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    occSim[r, c] = occ[r, c];

            for (int k = 1; k <= simItems.Count; k++)
            {
                var it = simItems[k - 1];
                FillCellsSim(it.Row, it.Col, it.RowSpan, it.ColSpan, occSim, false);
                if (TryPlaceSim(reqRows, reqCols, occSim, out row, out col, avoidClock))
                {
                    removeCount = k;
                    return true;
                }
            }
            return false;
        }

        private async Task<string> GetRandomUnusedPathForPlanAsync(HashSet<string> additionallyUsed)
        {
            int tries = GetImageTryCount();
            CleanupCooldown();
            var now = DateTime.UtcNow;
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (UsedPaths.Contains(p) || additionallyUsed.Contains(p)) { await Task.Yield(); continue; }
                if (_cooldown.TryGetValue(p, out var until) && until > now) { await Task.Yield(); continue; }
                return p;
            }
            for (int i = 0; i < tries; i++)
            {
                var p = ImageService.GetRandomImagePath();
                if (string.IsNullOrWhiteSpace(p)) return string.Empty;
                if (!UsedPaths.Contains(p) && !additionallyUsed.Contains(p)) return p;
                await Task.Yield();
            }
            return string.Empty;
        }

        private async Task PreloadImageUrlAsync(string url)
        {
            try { await JS.InvokeVoidAsync("window.app.preloadImage", url); } catch { }
        }

        private async Task WaitForGridReadyAsync(TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((Occupied is null || Cols <= 0 || Rows <= 0) && sw.Elapsed < timeout)
            {
                await Task.Delay(20);
            }
        }

        private async Task WaitForNextTickAsync(TiledItem? lastItem, CancellationToken token)
        {
            var delayTask = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, DelaySeconds)), token);
            if (lastItem?.AudioSrc is string audio && !string.IsNullOrWhiteSpace(audio) && AudioVolume > AudioSilenceEpsilon)
            {
                var audioTask = PlayAudioAndWaitAsync(audio, token);
                try
                {
                    await Task.WhenAll(delayTask, audioTask);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    await delayTask;
                }
                return;
            }
            await delayTask;
        }

        private async Task<double> PlayAudioAndWaitAsync(string audioSrc, CancellationToken token)
        {
            try
            {
                return await JS.InvokeAsync<double>("window.app.playAudioAndWait", token, audioSrc);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return 0;
            }
        }

        private async Task ApplyAudioVolumeToJsAsync()
        {
            try { await JS.InvokeVoidAsync("window.app.setAudioVolume", AudioVolume); } catch { }
        }

        private async Task StopAudioPlaybackAsync()
        {
            try { await JS.InvokeVoidAsync("window.app.stopAudioPlayback"); } catch { }
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

        private string? GetAudioUrlForImage(string imagePath)
        {
            var audioPath = FindCompanionAudioPath(imagePath);
            if (string.IsNullOrWhiteSpace(audioPath)) return null;
            var url = BuildVirtualHostUrl(audioPath);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }

        private string? FindCompanionAudioPath(string imagePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(imagePath);
                var name = Path.GetFileNameWithoutExtension(imagePath);
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name)) return null;
                foreach (var ext in AudioExtensions.Extensions)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private void UpdateClockText()
        {
            var now = DateTime.Now;
            ClockTime = now.ToString("HH:mm");
            var ja = CultureInfo.GetCultureInfo("ja-JP");
            var dow = now.ToString("ddd", ja);
            ClockDate = $"{now:MM/dd}({dow})";
        }

        private double ClockWidth => ClockBaseWidth * ClockScale;

        private double ClockHeight => ClockBaseHeight * ClockScale;

        private void ComputeClockReservedCells()
        {
            if (Rows <= 0 || Cols <= 0 || !ShowClock)
            {
                ClockCells = null;
                ClockOverlapped = false;
                return;
            }
            ClockCells = new bool[Rows, Cols];

            var normalizedCorner = NormalizeClockCorner(ClockCorner);

            // Clock rectangle in viewport px
            double width = Math.Min(ClockWidth, Math.Max(0, ViewportW));
            double height = Math.Min(ClockHeight, Math.Max(0, ViewportH));
            double cx1 = normalizedCorner switch
            {
                ClockCornerTopLeft or ClockCornerBottomLeft => ClockMarginHorizontal,
                ClockCornerTopRight or ClockCornerBottomRight => Math.Max(ClockMarginHorizontal, ViewportW - ClockMarginHorizontal - width),
                ClockCornerTopCenter or ClockCornerBottomCenter or ClockCornerCenter => Math.Max(ClockMarginHorizontal, (ViewportW - width) / 2.0),
                _ => ClockMarginHorizontal
            };
            double cy1 = normalizedCorner switch
            {
                ClockCornerTopLeft or ClockCornerTopRight or ClockCornerTopCenter => ClockMarginVertical,
                ClockCornerBottomLeft or ClockCornerBottomRight or ClockCornerBottomCenter => Math.Max(ClockMarginVertical, ViewportH - ClockMarginVertical - height),
                ClockCornerCenter => Math.Max(ClockMarginVertical, (ViewportH - height) / 2.0),
                _ => ClockMarginVertical
            };
            cx1 = Math.Clamp(cx1, 0, Math.Max(0, ViewportW - width));
            cy1 = Math.Clamp(cy1, 0, Math.Max(0, ViewportH - height));
            double cx2 = cx1 + width;
            double cy2 = cy1 + height;

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
            => ShowClock && ClockCells is not null && r >= 0 && r < Rows && c >= 0 && c < Cols && ClockCells[r, c];

        private bool IsOverlappingClock(int row, int col, int rowSpan, int colSpan)
        {
            if (!ShowClock || ClockCells is null) return false;
            for (int r = row; r < row + rowSpan; r++)
                for (int c = col; c < col + colSpan; c++)
                    if (IsClockCell(r, c)) return true;
            return false;
        }

        private void UpdateClockOverlap()
        {
            if (!ShowClock || Occupied is null || ClockCells is null) { ClockOverlapped = false; return; }
            bool any = false;
            for (int r = 0; r < Rows && !any; r++)
                for (int c = 0; c < Cols && !any; c++)
                    if (ClockCells[r, c] && Occupied[r, c]) any = true;
            ClockOverlapped = any;
        }

        private static string NormalizeClockCorner(string? corner)
        {
            if (string.IsNullOrWhiteSpace(corner)) return ClockCornerBottomLeft;
            if (corner.Equals(ClockCornerTopLeft, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopLeft;
            if (corner.Equals(ClockCornerTopRight, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopRight;
            if (corner.Equals(ClockCornerTopCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerTopCenter;
            if (corner.Equals(ClockCornerBottomRight, StringComparison.OrdinalIgnoreCase)) return ClockCornerBottomRight;
            if (corner.Equals(ClockCornerBottomCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerBottomCenter;
            if (corner.Equals(ClockCornerCenter, StringComparison.OrdinalIgnoreCase)) return ClockCornerCenter;
            return ClockCornerBottomLeft;
        }

        private string ClockCornerCssClass => NormalizeClockCorner(ClockCorner) switch
        {
            ClockCornerTopLeft => "top-left",
            ClockCornerTopRight => "top-right",
            ClockCornerTopCenter => "top-center",
            ClockCornerBottomRight => "bottom-right",
            ClockCornerBottomCenter => "bottom-center",
            ClockCornerCenter => "center",
            _ => "bottom-left"
        };
    }
}
