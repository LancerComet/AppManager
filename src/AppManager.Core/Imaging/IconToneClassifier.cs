namespace AppManager.Core.Imaging;

/// <summary>Whether an icon reads as mostly-white or mostly-black artwork,
/// so the UI can put it on a contrasting tile.</summary>
public enum IconTone
{
    Normal,
    Light,
    Dark,
}

/// <summary>
/// Classifies an icon's tone from premultiplied BGRA pixels. Transparent
/// pixels are ignored; the rest are un-premultiplied and scored on luminance.
/// An icon is Light when ≥85% of its visible pixels are light-gray-or-lighter
/// and low-saturation, Dark when ≥85% are near-black, else Normal.
/// </summary>
public static class IconToneClassifier
{
    public static IconTone Classify(ReadOnlySpan<byte> bgraPremultiplied, int width, int height)
    {
        var total = (long)width * height;
        if (total <= 0)
            return IconTone.Normal;

        long counted = 0, light = 0, dark = 0;
        var bytes = Math.Min(bgraPremultiplied.Length, (int)total * 4);
        for (var i = 0; i + 3 < bytes; i += 4)
        {
            var a = bgraPremultiplied[i + 3];
            if (a < 64)
                continue;
            counted++;

            // Un-premultiply: channel = color * a / 255  →  color01 = channel / a.
            var r = Math.Min(1.0, bgraPremultiplied[i + 2] / (double)a);
            var g = Math.Min(1.0, bgraPremultiplied[i + 1] / (double)a);
            var b = Math.Min(1.0, bgraPremultiplied[i] / (double)a);
            var l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));

            if (l >= 0.80 && max - min <= 0.15)
                light++;
            if (l <= 0.20)
                dark++;
        }

        if (counted * 100 < total) // fewer than 1% visible pixels
            return IconTone.Normal;
        if (light * 1.0 / counted >= 0.85)
            return IconTone.Light;
        if (dark * 1.0 / counted >= 0.85)
            return IconTone.Dark;
        return IconTone.Normal;
    }
}
