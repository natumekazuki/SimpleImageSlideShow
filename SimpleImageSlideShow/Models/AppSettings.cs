namespace SimpleImageSlideShow.Models
{
    public sealed class AppSettings
    {
        public uint DelaySeconds { get; set; } = 5;
        public uint ImageCount { get; set; } = 3;
        public string? DirectoryPath { get; set; }
        public string LastMode { get; set; } = "Slide";

        // Tiled mode settings
        public double TiledMinScale { get; set; } = 0.5;      // 0.1-1.0 (relative to fit)
        public double TiledMaxScale { get; set; } = 1.0;      // 0.1-1.0 (relative to fit)
        public int TiledCols { get; set; } = 6;                // current-screen columns (square tiles)
        public int MinTilePx { get; set; } = 128;              // min tile width (px)
        public int TiledReuseTtlSeconds { get; set; } = 120;   // TTL to reuse an image in Tiled
    }
}
