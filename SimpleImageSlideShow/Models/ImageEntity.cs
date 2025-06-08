namespace SimpleImageSlideShow.Models
{
    internal sealed class ImageEntity : IImageEntity
    {
        public required string FilePath { get; init; }

        public required byte[] BytesImage { get; init; }

        public required double Width { get; init; }

        public required double Height { get; init; }

    }
}
