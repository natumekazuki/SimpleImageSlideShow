using SimpleImageSlideShow.Models;
using Xunit;

namespace SimpleImageSlideShow.Tests;

public sealed class DelayRangeTests
{
    [Theory]
    [InlineData(0u, 0u, 0u, 1u)]
    [InlineData(0u, 1u, 0u, 1u)]
    [InlineData(5u, 1u, 5u, 5u)]
    [InlineData(61u, 90u, 60u, 60u)]
    public void Normalize_ClampsConfiguredBounds(uint min, uint max, uint expectedMin, uint expectedMax)
    {
        var range = DelayRange.Normalize(min, max);

        Assert.Equal(expectedMin, range.MinSeconds);
        Assert.Equal(expectedMax, range.MaxSeconds);
    }

    [Fact]
    public void NextDelaySeconds_UsesFractionalRange()
    {
        var range = DelayRange.Normalize(0, 1);
        var random = new Random(12345);

        var values = Enumerable.Range(0, 20)
            .Select(_ => range.NextDelaySeconds(random))
            .ToArray();

        Assert.All(values, value => Assert.InRange(value, 0, 1));
        Assert.Contains(values, value => value > 0 && value < 1);
    }

    [Fact]
    public void NextDelaySeconds_ReturnsFixedValueWhenBoundsMatch()
    {
        var range = DelayRange.Normalize(7, 7);

        Assert.Equal(7, range.NextDelaySeconds(new Random(12345)));
    }
}
