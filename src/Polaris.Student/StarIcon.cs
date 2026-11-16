namespace Polaris.Student;

/// <summary>The picture of the icon, drawn by code: the Pole Star – four long rays and four short ones – on a deep blue
/// disc. Every pixel is the average of 4×4 samples, so the edges are smooth at any size the screen asks for.</summary>
public static class StarIcon
{
    public const uint Sky = 0x12315C;
    public const uint Light = 0xF5F7FF;
    private const int Samples = 4;

    /// <summary>The 16 corners of the star, clockwise from the top: long ray, valley, short ray, valley, …</summary>
    private static readonly (double X, double Y)[] Star = Enumerable.Range(0, 16).Select(k =>
    {
        var radius = (k % 4) switch { 0 => 0.82, 2 => 0.42, _ => 0.15 };
        var angle = (k * 22.5 - 90) * Math.PI / 180;
        return (radius * Math.Cos(angle), radius * Math.Sin(angle));
    }).ToArray();

    /// <summary>size × size pixels, row by row from the top, as 0xAARRGGBB (alpha not premultiplied).</summary>
    public static uint[] Pixels(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        var pixels = new uint[size * size];
        const int all = Samples * Samples;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int disc = 0, star = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        // a sample in coordinates where the disc has radius 1 and the centre is 0
                        var u = ((x + (sx + 0.5) / Samples) / size * 2) - 1;
                        var v = ((y + (sy + 0.5) / Samples) / size * 2) - 1;
                        if (u * u + v * v <= 0.96 * 0.96)
                        {
                            disc++;
                            star += Inside(u, v) ? 1 : 0;
                        }
                    }
                }

                if (disc == 0)
                {
                    continue;
                }

                var alpha = (uint)((disc * 255 + all / 2) / all);
                pixels[(y * size) + x] = (alpha << 24) | Mix(Light, Sky, star, disc);
            }
        }

        return pixels;
    }

    /// <summary>The colour of a pixel: the share of star samples among the samples on the disc.</summary>
    private static uint Mix(uint a, uint b, int partA, int whole)
    {
        uint Channel(int shift) => (uint)(((((a >> shift) & 0xFF) * partA) + (((b >> shift) & 0xFF) * (whole - partA)) + (whole / 2)) / whole);
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>Even–odd test: a ray to the right crosses the edge of the star an odd number of times.</summary>
    private static bool Inside(double u, double v)
    {
        var inside = false;
        for (int i = 0, j = Star.Length - 1; i < Star.Length; j = i++)
        {
            var (xi, yi) = Star[i];
            var (xj, yj) = Star[j];
            if ((yi > v) != (yj > v) && u < ((xj - xi) * (v - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
