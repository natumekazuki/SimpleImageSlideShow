namespace SimpleImageSlideShow.Models
{
    public sealed class AppSettings
    {
        public uint DelaySeconds { get; set; } = 5;
        public uint ImageCount { get; set; } = 3;
        public string? DirectoryPath { get; set; }
        public string LastMode { get; set; } = "Slide";

        // Tiled mode settings
        public int TiledFillTargetPercent { get; set; } = 100; // 70-100
        public double TiledMinScale { get; set; } = 0.5;      // 0.1-1.0
        public int TiledCols { get; set; } = 6;                // current-screen columns (square tiles)
        public int MinTilePx { get; set; } = 128;              // min tile width (px)
        public double PanelLeft { get; set; } = 10;
        public double PanelTop { get; set; } = 10;
        public double PanelWidth { get; set; } = 560;
        public double PanelHeight { get; set; } = 260;
    }
}
