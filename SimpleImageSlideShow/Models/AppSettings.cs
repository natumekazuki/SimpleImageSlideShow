namespace SimpleImageSlideShow.Models
{
    public sealed class AppSettings
    {
        public uint DelaySeconds { get; set; } = 5;
        public string? DirectoryPath { get; set; }
        public string WindowDisplayMode { get; set; } = "FullScreen";

        public double TiledMinScale { get; set; } = 0.5;      // 0.1-1.0 (relative to fit)
        public double TiledMaxScale { get; set; } = 1.0;      // 0.1-1.0 (relative to fit)
        public int TiledCols { get; set; } = 6;                // current-screen columns (square tiles)
        public int MinTilePx { get; set; } = 128;              // min tile width (px)
        public int TiledReuseTtlSeconds { get; set; } = 120;   // TTL to reuse an image in Tiled
        public bool ShowTiledClock { get; set; } = true;       // Toggle clock overlay
        public string TiledClockCorner { get; set; } = "BottomLeft"; // Clock corner selection
        public double TiledClockScale { get; set; } = 1.0;     // Clock scale multiplier
        public bool AvoidTiledClockOverlap { get; set; } = true; // Keep images off the clock overlay

        public uint RandomScaleTries { get; set; } = 10;      // number of tries to find a random scale that fits

        public double AudioVolumePercent { get; set; } = 0;    // 0-100 slider, start muted

        public string BackgroundColor { get; set; } = "#D3D3D3";
    }
}
