// <copyright file="RadialGeometry.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu
{
    using System;
    using System.Windows;

    /// <summary>
    /// Layout maths of the radial menu. Angles are in degrees, 0 is 12 o'clock and they grow clockwise.
    /// </summary>
    public static class RadialGeometry
    {
        // Device independent pixels.
        public const double WindowSize = 320;
        public const double OuterRadius = 150;
        public const double InnerRadius = 55;

        /// <summary>
        /// Slice <paramref name="index"/> is centered on index * 360 / sliceCount. The start angle is in [0, 360).
        /// </summary>
        public static (double StartDegrees, double SweepDegrees) GetSliceAngles(int index, int sliceCount)
        {
            if (sliceCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sliceCount), "There must be at least one slice.");
            }

            double sweep = 360d / sliceCount;
            double start = NormalizeDegrees((index * sweep) - (sweep / 2));
            return (start, sweep);
        }

        /// <summary>
        /// Slice under the point (relative to the centre of the ring, y pointing down), or -1 over the hub or outside the ring.
        /// </summary>
        public static int HitTestSlice(double x, double y, int sliceCount, double innerRadius, double outerRadius)
        {
            if (sliceCount <= 0)
            {
                return -1;
            }

            double distance = Math.Sqrt((x * x) + (y * y));
            if (distance < innerRadius || distance > outerRadius)
            {
                return -1;
            }

            double sweep = 360d / sliceCount;
            double angle = NormalizeDegrees(Math.Atan2(x, -y) * 180d / Math.PI);
            int index = (int)Math.Floor((angle + (sweep / 2)) / sweep);
            return index % sliceCount;
        }

        /// <summary>
        /// Window rectangle (physical pixels) centered on the cursor and moved inside the work area. A window larger than
        /// the work area is aligned on its top left corner.
        /// </summary>
        public static Rect ClampToWorkArea(Point cursorPx, double windowSizePx, Rect workAreaPx)
        {
            double left = Clamp(cursorPx.X - (windowSizePx / 2), workAreaPx.Left, workAreaPx.Right - windowSizePx);
            double top = Clamp(cursorPx.Y - (windowSizePx / 2), workAreaPx.Top, workAreaPx.Bottom - windowSizePx);
            return new Rect(left, top, windowSizePx, windowSizePx);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum)
            {
                return minimum;
            }

            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360d;
            if (normalized < 0)
            {
                normalized += 360d;
            }

            return normalized;
        }
    }
}
