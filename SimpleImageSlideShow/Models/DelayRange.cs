namespace SimpleImageSlideShow.Models
{
    public readonly record struct DelayRange(uint MinSeconds, uint MaxSeconds)
    {
        public const uint MaxAllowedSeconds = 60;

        public static DelayRange Normalize(uint minSeconds, uint maxSeconds)
        {
            var min = Math.Min(MaxAllowedSeconds, minSeconds);
            var max = Math.Max(1u, Math.Min(MaxAllowedSeconds, maxSeconds));
            if (max < min) max = min;
            return new DelayRange(min, max);
        }

        public double NextDelaySeconds(Random? random = null)
        {
            if (MinSeconds == MaxSeconds) return MinSeconds;
            random ??= Random.Shared;
            return MinSeconds + random.NextDouble() * (MaxSeconds - MinSeconds);
        }
    }
}
