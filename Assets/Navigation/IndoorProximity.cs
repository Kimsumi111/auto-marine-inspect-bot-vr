namespace ShipRobot.Navigation
{
    public static class IndoorProximity
    {
        public static bool Contains(float dx, float dz, float radius) =>
            radius > 0f && !float.IsInfinity(radius) && dx * dx + dz * dz <= radius * radius;
    }
}
