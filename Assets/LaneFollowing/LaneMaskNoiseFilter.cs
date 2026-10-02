using System;

namespace ShipRobot.LaneFollowing
{
    /// <summary>Removes small 8-connected islands; preserves thin, elongated markings.</summary>
    public sealed class LaneMaskNoiseFilter
    {
        private bool[] visited = Array.Empty<bool>();
        private int[] queue = Array.Empty<int>();

        public int Apply(bool[] mask, int width, int height, int minimumArea,
            int minimumLineLength, float minimumLineAspectRatio)
        {
            if (mask == null || width <= 0 || height <= 0 || mask.Length != width * height)
                throw new ArgumentException("Mask dimensions must match.");
            if (visited.Length != mask.Length)
            {
                visited = new bool[mask.Length];
                queue = new int[mask.Length];
            }
            else Array.Clear(visited, 0, visited.Length);
            int removed = 0;
            for (int seed = 0; seed < mask.Length; seed++)
            {
                if (!mask[seed] || visited[seed]) continue;
                int head = 0, count = 1;
                queue[0] = seed;
                visited[seed] = true;
                int minX = width, maxX = 0, minY = height, maxY = 0;
                while (head < count)
                {
                    int index = queue[head++], x = index % width, y = index / width;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        int next = ny * width + nx;
                        if (!mask[next] || visited[next]) continue;
                        visited[next] = true;
                        queue[count++] = next;
                    }
                }
                // Principal-axis spread also preserves diagonal lines, unlike bounding-box aspect ratios.
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                for (int i = 0; i < count; i++)
                {
                    double x = queue[i] % width, y = queue[i] / width;
                    sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                }
                double vx = Math.Max(0, sxx / count - sx * sx / count / count);
                double vy = Math.Max(0, syy / count - sy * sy / count / count);
                double cov = sxy / count - sx * sy / count / count;
                double delta = Math.Sqrt((vx - vy) * (vx - vy) + 4 * cov * cov);
                double major = (vx + vy + delta) / 2;
                double minor = Math.Max(0, (vx + vy - delta) / 2);
                double aspect = Math.Sqrt((major + 0.25) / (minor + 0.25));
                double extent = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) + 1;
                bool line = count >= 4 && extent >= minimumLineLength && aspect >= minimumLineAspectRatio;
                if (count >= minimumArea || line) continue;
                for (int i = 0; i < count; i++) mask[queue[i]] = false;
                removed += count;
            }
            return removed;
        }
    }
}
