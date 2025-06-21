namespace SimpleImageSlideShow.Models
{
    public interface IImageEntity
    {
        public string FilePath { get; }

        public byte[] BytesImage { get; }

        public double Width { get; }

        public double Height { get; }

        public bool IsLandscape => Width > Height;

        public string ImageUrl => "data:image/png;base64," + Convert.ToBase64String(BytesImage);

        public string CssClass { get; }
    }
}
