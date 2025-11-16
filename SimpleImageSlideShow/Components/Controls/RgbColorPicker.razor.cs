using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace SimpleImageSlideShow.Components.Controls
{
    public partial class RgbColorPicker
    {
        private record ChannelDescriptor(string Key, string Label, string AriaLabel);

        private static readonly ChannelDescriptor[] Channels =
        [
        new ChannelDescriptor("R", "Red", "Red level"),
        new ChannelDescriptor("G", "Green", "Green level"),
        new ChannelDescriptor("B", "Blue", "Blue level")
    ];

        [Parameter]
        public string Value { get; set; } = "#D3D3D3";

        [Parameter]
        public EventCallback<string> ValueChanged { get; set; }

        [Parameter]
        public string? AriaLabel { get; set; }

        private string PickerAriaLabel => string.IsNullOrWhiteSpace(AriaLabel) ? "RGB color picker" : AriaLabel!;

        private int Red { get; set; } = 211;
        private int Green { get; set; } = 211;
        private int Blue { get; set; } = 211;
        private string _hexValue = "#D3D3D3";
        private readonly string _hexInputId = $"hex-input-{Guid.NewGuid():N}";

        protected override void OnParametersSet()
        {
            var normalized = NormalizeHex(this.Value);
            if (!string.Equals(normalized, _hexValue, StringComparison.OrdinalIgnoreCase))
            {
                _hexValue = normalized;
                (this.Red, this.Green, this.Blue) = HexToRgb(_hexValue);
            }
        }

        private async Task OnChannelInput(string channelKey, ChangeEventArgs e)
        {
            if (!int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            {
                return;
            }

            var clamped = Math.Clamp(raw, 0, 255);

            switch (channelKey)
            {
                case "R":
                    if (clamped == this.Red) return;
                    this.Red = clamped;
                    break;
                case "G":
                    if (clamped == this.Green) return;
                    this.Green = clamped;
                    break;
                case "B":
                    if (clamped == this.Blue) return;
                    this.Blue = clamped;
                    break;
                default:
                    return;
            }

            await CommitColorAsync();
        }

        private async Task OnHexChanged(ChangeEventArgs e)
        {
            var normalized = NormalizeHex(e.Value?.ToString());
            if (string.Equals(normalized, _hexValue, StringComparison.OrdinalIgnoreCase)) return;
            _hexValue = normalized;
            (this.Red, this.Green, this.Blue) = HexToRgb(_hexValue);
            await this.ValueChanged.InvokeAsync(_hexValue);
        }

        private async Task CommitColorAsync()
        {
            _hexValue = $"#{this.Red:X2}{this.Green:X2}{this.Blue:X2}";
            await this.ValueChanged.InvokeAsync(_hexValue);
        }

        private static (int R, int G, int B) HexToRgb(string value)
        {
            var r = Convert.ToInt32(value.Substring(1, 2), 16);
            var g = Convert.ToInt32(value.Substring(3, 2), 16);
            var b = Convert.ToInt32(value.Substring(5, 2), 16);
            return (r, g, b);
        }

        private static string NormalizeHex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "#D3D3D3";
            var hex = value.Trim();
            if (!hex.StartsWith('#')) hex = $"#{hex}";
            if (hex.Length == 4)
            {
                hex = $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
            }

            if (hex.Length != 7) return "#D3D3D3";
            if (!int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return "#D3D3D3";
            }

            return $"#{hex[1..].ToUpperInvariant()}";
        }

        private static int GetChannelValue(string key, int r, int g, int b)
            => key switch
            {
                "R" => r,
                "G" => g,
                "B" => b,
                _ => 0
            };

        private int GetChannelValue(string key) => GetChannelValue(key, this.Red, this.Green, this.Blue);
    }
}
