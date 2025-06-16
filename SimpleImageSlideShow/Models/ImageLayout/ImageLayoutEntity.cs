namespace SimpleImageSlideShow.Models.ImageLayout
{
    internal class ImageLayoutEntity
    {
        public required string Id { get; init; }
        public required uint ImageCount { get; set; }
        public required uint WideImageCount { get; init; }
        public uint TallImageCount  => ImageCount - WideImageCount;
    }
}
