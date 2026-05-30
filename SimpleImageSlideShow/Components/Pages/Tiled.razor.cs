using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;
using SimpleImageSlideShow.Services;

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
        public required IFolderPickerService FolderPickerService { get; init; }

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

        private readonly record struct ViewportSize(double Width, double Height);

        private List<TiledItem> Items { get; set; } = [];
        private HashSet<string> UsedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        private uint MinDelaySeconds { get; set; } = 5;
        private uint MaxDelaySeconds { get; set; } = 5;
        private double MinScale { get; set; } = 0.5;
        private double MaxScale { get; set; } = 1.0;
        private string BackgroundColor { get; set; } = DefaultBackgroundColor;
        private string? DirectoryPath { get; set; }
        private IReadOnlyList<SettingsProfileSummary> SettingsProfiles { get; set; } = [];
        private string? ActiveSettingsProfileId { get; set; }
        private string ActiveSettingsProfileName { get; set; } = "Default";
        private bool IsProfileChanging { get; set; }
        private bool CanDeleteActiveProfile => SettingsProfiles.Count > 1 && !string.IsNullOrWhiteSpace(ActiveSettingsProfileId);
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
        private static readonly ClockCornerChoice[] ClockCornerChoices =
        {
            new(ClockCornerTopLeft, "Top Left"),
            new(ClockCornerTopRight, "Top Right"),
            new(ClockCornerTopCenter, "Top Center"),
            new(ClockCornerBottomLeft, "Bottom Left"),
            new(ClockCornerBottomRight, "Bottom Right"),
            new(ClockCornerBottomCenter, "Bottom Center"),
            new(ClockCornerCenter, "Center")
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
    }
}
