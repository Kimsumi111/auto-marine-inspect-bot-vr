namespace ShipRobot.Navigation
{
    // Counts observations, not Unity Update calls. Invalid observations break the streak.
    public sealed class FreshDetectionStreak
    {
        private double lastTimestamp = -1d;
        public int Count { get; private set; }
        public void Reset() { lastTimestamp = -1d; Count = 0; }
        public int Observe(double timestamp, bool valid)
        {
            if (!valid) Count = 0;
            if (double.IsNaN(timestamp) || double.IsInfinity(timestamp) || timestamp <= lastTimestamp)
                return Count;
            lastTimestamp = timestamp;
            if (valid) Count++;
            return Count;
        }
    }
}
