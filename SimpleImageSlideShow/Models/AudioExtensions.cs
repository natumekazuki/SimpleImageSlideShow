namespace SimpleImageSlideShow.Models
{
    internal static class AudioExtensions
    {
        internal static readonly string[] Extensions =
        [
            ".mp3",
            ".m4a",
            ".aac",
            ".wav",
            ".wma",
            ".ogg",
            ".oga",
            ".flac"
        ];

        internal static bool IsAudioFile(string fileName)
            => Extensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
