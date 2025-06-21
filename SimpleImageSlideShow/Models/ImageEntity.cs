namespace SimpleImageSlideShow.Models
{
    internal sealed class ImageEntity : IImageEntity
    {
        public string Id = Guid.NewGuid().ToString();
        public required string FilePath { get; init; }

        public required byte[] BytesImage { get; init; }

        public required double Width { get; init; }

        public required double Height { get; init; }

        public required string CssClass { get; init; }
    }
}
