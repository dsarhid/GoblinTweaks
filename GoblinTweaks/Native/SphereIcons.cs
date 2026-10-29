using System.Numerics;
using Dalamud.Interface.Textures;

namespace GoblinTweaks.Native;

/// <summary>
/// Game icons bent and shaded like a sphere, for Goblin Hotbar Icons. Pure pixel work, done off the framework thread;
/// the icon is read back through Dalamud, so a mod that replaces it is respected.
/// </summary>
internal static class SphereIcons
{
    /// <summary>
    /// The pixels (RGBA, square) of the sphere of an icon, and their size; a size of 0 and no pixels if it could not
    /// be read. Item icons carry their HQ version as one million more.
    /// </summary>
    public static async Task<(int Size, byte[]? Pixels)> MakeAsync(uint iconId, float strength, float crop)
    {
        try
        {
            var hq = iconId >= 1_000_000;
            var lookup = new GameIconLookup(hq ? iconId - 1_000_000 : iconId, hq, true);
            var wrap = await Svc.Textures.GetFromGameIcon(lookup).RentAsync().ConfigureAwait(false);
            var (spec, data) = await Svc.TextureReadback.GetRawImageAsync(
                wrap, new TextureModificationArgs { DxgiFormat = 28 /* R8G8B8A8_UNORM */ }, leaveWrapOpen: false).ConfigureAwait(false);

            var size = Math.Clamp(spec.Width, 16, 256);
            return (size, Paint(data, spec.Width, spec.Height, spec.Pitch, size, strength, crop));
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "GoblinHotbarIcons: could not bend icon {icon}; it stays flat.", iconId);
            return (0, null);
        }
    }

    /// <summary>
    /// An icon bent like a sphere seen from the front: sharp and a little bigger in the middle, squeezed towards the
    /// edge, darker on the side away from the light and with a highlight near the top. <paramref name="strength"/>
    /// (0 to 1) goes from a flat disc to a full sphere; <paramref name="crop"/> is how much of the icon the circle shows.
    /// </summary>
    private static byte[] Paint(byte[] source, int width, int height, int pitch, int size, float strength, float crop)
    {
        var pixels = new byte[size * size * 4];
        var light = Vector3.Normalize(new Vector3(-0.45f, -0.55f, 0.7f));
        var half = Vector3.Normalize(light + Vector3.UnitZ);
        var frontLight = 0.4f + 0.6f * light.Z;
        var twoOverPi = 2f / MathF.PI;

        // The outermost pixels of many icons are a thin light border: left out, or they would line the rim.
        crop *= 0.96f;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size * 2f - 1f;
                var v = (y + 0.5f) / size * 2f - 1f;
                var rho = MathF.Sqrt(u * u + v * v);
                var edge = Math.Clamp((1f - rho) * size / 2f + 0.5f, 0f, 1f);
                if (edge <= 0f)
                    continue;

                // Where on the flat icon this point of the sphere comes from: asin squeezes the edge.
                var r = MathF.Min(rho, 1f);
                var bent = (1f - strength) * r + strength * twoOverPi * MathF.Asin(r);
                var factor = r > 0.0001f ? bent / r : 1f - strength + strength * twoOverPi;
                var su = (u * factor * crop + 1f) / 2f * width - 0.5f;
                var sv = (v * factor * crop + 1f) / 2f * height - 0.5f;
                var color = Sample(source, width, height, pitch, su, sv);

                // Light: a little darker away from it, a soft highlight towards it.
                var normal = new Vector3(u, v, MathF.Sqrt(MathF.Max(0f, 1f - r * r)));
                var diffuse = MathF.Max(0f, Vector3.Dot(normal, light));
                var shade = 1f + strength * ((0.4f + 0.6f * diffuse) / frontLight - 1f);
                var shine = MathF.Pow(MathF.Max(0f, Vector3.Dot(normal, half)), 48f) * 0.55f * strength;

                var i = (y * size + x) * 4;
                pixels[i]     = (byte)Math.Clamp(color.X * shade + shine * 255f, 0f, 255f);
                pixels[i + 1] = (byte)Math.Clamp(color.Y * shade + shine * 255f, 0f, 255f);
                pixels[i + 2] = (byte)Math.Clamp(color.Z * shade + shine * 255f, 0f, 255f);
                pixels[i + 3] = (byte)Math.Clamp(color.W * edge, 0f, 255f);
            }
        }

        return pixels;
    }

    /// <summary>The color of an RGBA image between its pixels, with the edge pixels repeated outside it.</summary>
    private static Vector4 Sample(byte[] source, int width, int height, int pitch, float x, float y)
    {
        x = Math.Clamp(x, 0f, width - 1f);
        y = Math.Clamp(y, 0f, height - 1f);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = x - x0;
        var fy = y - y0;

        Vector4 Pixel(int px, int py)
        {
            var i = py * pitch + px * 4;
            return new Vector4(source[i], source[i + 1], source[i + 2], source[i + 3]);
        }

        var top = Vector4.Lerp(Pixel(x0, y0), Pixel(x1, y0), fx);
        var bottom = Vector4.Lerp(Pixel(x0, y1), Pixel(x1, y1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }
}
