namespace GoblinTweaks.Glamour;

/// <summary>
/// Color math for Goblin Glamour. Everything is measured in OKLab / OKLCh, where distances and hues
/// follow what the eye sees much better than in RGB.
/// </summary>
public static class ColorMath
{
    /// <summary>Below this chroma a color counts as neutral (gray, black, white): it goes with everything.</summary>
    private const float NeutralChroma = 0.04f;

    /// <summary>Harmony of a neutral color with anything.</summary>
    private const float NeutralHarmony = 0.8f;

    public static (float L, float A, float B) ToOklab(Rgb c)
    {
        var r = Linear(c.R);
        var g = Linear(c.G);
        var b = Linear(c.B);

        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);

        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>The sRGB color of an OKLCh color (lightness 0..1, chroma, hue in degrees), clamped to the sRGB range.</summary>
    public static Rgb FromOklch(float lightness, float chroma, float hueDegrees)
    {
        var h = hueDegrees * (MathF.PI / 180f);
        var a = chroma * MathF.Cos(h);
        var b = chroma * MathF.Sin(h);

        var l = Cube(lightness + 0.3963377774f * a + 0.2158037573f * b);
        var m = Cube(lightness - 0.1055613458f * a - 0.0638541728f * b);
        var s = Cube(lightness - 0.0894841775f * a - 1.2914855480f * b);

        return new Rgb(Encode(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s),
                       Encode(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s),
                       Encode(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s));

        static float Cube(float x) => x * x * x;
    }

    /// <summary>OKLab lightness, 0 (black) .. 1 (white).</summary>
    public static float Lightness(Rgb c) => ToOklab(c).L;

    /// <summary>Euclidean distance in OKLab (0 = same color, ~1 = black vs white).</summary>
    public static float Distance(Rgb a, Rgb b) => Distance(ToOklab(a), ToOklab(b));

    /// <summary>OKLCh hue in degrees, [0, 360).</summary>
    public static float Hue(Rgb c)
    {
        var (_, a, b) = ToOklab(c);
        var degrees = MathF.Atan2(b, a) * (180f / MathF.PI);
        return degrees < 0 ? degrees + 360f : degrees;
    }

    /// <summary>OKLCh chroma (0 = gray; vivid colors reach ~0.3).</summary>
    public static float Chroma(Rgb c)
    {
        var (_, a, b) = ToOklab(c);
        return MathF.Sqrt(a * a + b * b);
    }

    /// <summary>
    /// How well two colors go together, 0..1: high for close hues (analogous / monochrome, up to 30° apart)
    /// and for complementary hues (150°–210° apart), low for the 60°–120° clashes. A neutral color goes with
    /// anything (<see cref="NeutralHarmony"/>).
    /// </summary>
    public static float Harmony(Rgb a, Rgb b)
    {
        if (Chroma(a) < NeutralChroma || Chroma(b) < NeutralChroma)
            return NeutralHarmony;

        var delta = MathF.Abs(Hue(a) - Hue(b));
        if (delta > 180f)
            delta = 360f - delta;

        return delta switch
        {
            <= 30f  => 1f - delta / 30f * 0.1f,                 // 1.0 → 0.9
            <= 60f  => Lerp(0.9f, 0.3f, (delta - 30f) / 30f),   // fading into the clash
            <= 120f => 0.3f,                                    // clash
            <= 150f => Lerp(0.3f, 0.85f, (delta - 120f) / 30f), // towards complementary
            _       => 0.85f + (delta - 150f) / 30f * 0.15f,    // 0.85 → 1.0
        };
    }

    /// <summary>
    /// The two main colors of an RGBA image (an item icon): k-means with 3 clusters over the opaque pixels
    /// (alpha ≥ 128), in OKLab, 8 iterations, deterministic. Returns the mean colors of the two biggest clusters.
    /// A fully transparent image gives mid gray twice.
    /// </summary>
    public static (Rgb Primary, Rgb Secondary) DominantColors(ReadOnlySpan<byte> rgba, int width, int height)
    {
        const int maxSamples = 4096, k = 3, iterations = 8;

        var pixelCount = Math.Min(width * height, rgba.Length / 4);
        var step = Math.Max(1, pixelCount / maxSamples);
        var colors = new List<Rgb>();
        var labs   = new List<(float L, float A, float B)>();
        for (var i = 0; i < pixelCount; i += step)
        {
            if (rgba[i * 4 + 3] < 128)
                continue;
            var c = new Rgb(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            colors.Add(c);
            labs.Add(ToOklab(c));
        }

        if (colors.Count == 0)
            return (new Rgb(128, 128, 128), new Rgb(128, 128, 128));

        // Deterministic seeds: the first pixel, then each time the pixel farthest from the seeds so far.
        var centers = new List<(float L, float A, float B)> { labs[0] };
        while (centers.Count < k)
        {
            var best = -1f;
            var bestIndex = 0;
            for (var i = 0; i < labs.Count; i++)
            {
                var nearest = centers.Min(center => Distance(labs[i], center));
                if (nearest > best)
                {
                    best = nearest;
                    bestIndex = i;
                }
            }
            centers.Add(labs[bestIndex]);
        }

        var assignment = new int[labs.Count];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var i = 0; i < labs.Count; i++)
                assignment[i] = Nearest(labs[i], centers);

            for (var c = 0; c < k; c++)
            {
                float l = 0, a = 0, b = 0;
                var n = 0;
                for (var i = 0; i < labs.Count; i++)
                {
                    if (assignment[i] != c)
                        continue;
                    l += labs[i].L; a += labs[i].A; b += labs[i].B;
                    n++;
                }
                if (n > 0)
                    centers[c] = (l / n, a / n, b / n);
            }
        }

        // Mean sRGB color of each cluster, biggest first.
        var clusters = Enumerable.Range(0, k)
            .Select(c => (Count: assignment.Count(x => x == c), Color: MeanColor(colors, assignment, c)))
            .Where(cluster => cluster.Count > 0)
            .OrderByDescending(cluster => cluster.Count)
            .ToList();

        return (clusters[0].Color, clusters.Count > 1 ? clusters[1].Color : clusters[0].Color);
    }

    private static Rgb MeanColor(List<Rgb> colors, int[] assignment, int cluster)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i < colors.Count; i++)
        {
            if (assignment[i] != cluster)
                continue;
            r += colors[i].R; g += colors[i].G; b += colors[i].B;
            n++;
        }
        return n == 0 ? default : new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static int Nearest((float L, float A, float B) lab, List<(float L, float A, float B)> centers)
    {
        var best = 0;
        for (var c = 1; c < centers.Count; c++)
            if (Distance(lab, centers[c]) < Distance(lab, centers[best]))
                best = c;
        return best;
    }

    private static float Distance((float L, float A, float B) x, (float L, float A, float B) y)
    {
        var dl = x.L - y.L;
        var da = x.A - y.A;
        var db = x.B - y.B;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    private static float Linear(byte channel)
    {
        var c = channel / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Encode(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var encoded = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)MathF.Round(Math.Clamp(encoded, 0f, 1f) * 255f);
    }

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;
}
