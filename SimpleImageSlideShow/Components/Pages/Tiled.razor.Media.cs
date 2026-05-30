
using Microsoft.JSInterop;
using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Components.Pages
{
    public sealed partial class Tiled
    {
        private async Task<double> PlayAudioAndWaitAsync(string audioSrc, CancellationToken token)
        {
            try
            {
                return await JS.InvokeAsync<double>("window.app.playAudioAndWait", token, audioSrc);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return 0;
            }
        }

        private async Task ApplyAudioVolumeToJsAsync()
        {
            try { await JS.InvokeVoidAsync("window.app.setAudioVolume", AudioVolume); } catch { }
        }

        private async Task StopAudioPlaybackAsync()
        {
            try { await JS.InvokeVoidAsync("window.app.stopAudioPlayback"); } catch { }
        }

        private string BuildVirtualHostUrl(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) return string.Empty;
            string rel = Path.GetRelativePath(DirectoryPath, absolutePath).Replace('\\', '/');
            return $"https://{HostName}/{rel}";
        }

        private string? GetAudioUrlForImage(string imagePath)
        {
            var audioPath = FindCompanionAudioPath(imagePath);
            if (string.IsNullOrWhiteSpace(audioPath)) return null;
            var url = BuildVirtualHostUrl(audioPath);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }

        private string? FindCompanionAudioPath(string imagePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(imagePath);
                var name = Path.GetFileNameWithoutExtension(imagePath);
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name)) return null;
                foreach (var ext in AudioExtensions.Extensions)
                {
                    var candidate = Path.Combine(dir, name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

    }
}
