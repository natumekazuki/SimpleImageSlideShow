namespace SimpleImageSlideShow.Models
{
    internal static class ImageExtensions
    {
        /// <summary>
        /// 画像ファイルの拡張子
        /// </summary>
        internal static List<string> Extensions { get; } =
        [
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".tif",
            ".tiff",
            ".webp",
            ".svg",
            ".ico"
        ];

        /// <summary>
        /// 画像ファイルかどうか
        /// </summary>
        internal static bool IsImageFile(this string fileName)
        {
            return Extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }
    }
}
