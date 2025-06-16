using SimpleImageSlideShow.Models;

namespace SimpleImageSlideShow.Services
{
    public class FrameService
    {
        private readonly Random random = new();

        public string GetFrameCss()
        {
            int index = random.Next(Frames.FrameClasses.Count);
            return Frames.FrameClasses[index];
        }
    }
}
