using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleImageSlideShow.Models
{
    public interface IImageEntity
    {
        public string FilePath { get; }

        public byte[] BytesImage { get; }

        private const double Num = 0.5;

        public double Width { get; }

        public double MinWidth => 200;

        public double Height { get; }

        public double MinHeight => Height / 200 * AspectRatio;

        public double AspectRatio => Width > 0 ? Height / Width : 0.0;

        public string ImageUrl => "data:image/png;base64," + Convert.ToBase64String(BytesImage);
    }
}
