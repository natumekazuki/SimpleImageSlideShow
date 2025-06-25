namespace SimpleImageSlideShow.Models
{
    public interface IImageEntity
    {
        public string FilePath { get; }

        public byte[] BytesImage { get; }

        public double Width { get; }

        public double Height { get; }

        public double AspectRatio => Height / Width;

        public string ImageUrl => "data:image/png;base64," + Convert.ToBase64String(BytesImage);

        public string CssClass { get; }

        public double Offset { get; }

        string CssVariables => $"--aspect-ratio:{AspectRatio}; --offset:{Offset};";
    }
}
