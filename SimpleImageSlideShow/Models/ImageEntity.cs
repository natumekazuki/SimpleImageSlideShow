namespace SimpleImageSlideShow.Models
{
    internal sealed class ImageEntity
    {
        public string FilePath { get; init; } = string.Empty;

        public double Width { get; init; } = 0.0;

        public double Height { get; init; } = 0.0;

        public double AspectRatio => Width > 0 ? Height / Width : 0.0;
    }
}
