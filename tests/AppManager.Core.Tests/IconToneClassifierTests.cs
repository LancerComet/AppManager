using AppManager.Core.Imaging;
using Xunit;

namespace AppManager.Core.Tests;

public class IconToneClassifierTests
{
    private const int Size = 32;

    private static byte[] Buffer(int pixels, Func<int, (byte B, byte G, byte R, byte A)> px)
    {
        var buf = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var (b, g, r, a) = px(i);
            buf[i * 4] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static byte[] Solid(byte b, byte g, byte r, byte a)
        => Buffer(Size * Size, _ => (b, g, r, a));

    private static byte[] Glyph((byte B, byte G, byte R, byte A) bg, (byte B, byte G, byte R, byte A) fg, int fgPixels)
        => Buffer(Size * Size, i => i < fgPixels ? fg : bg);

    [Fact]
    public void WhiteGlyph_OnTransparent_IsLight()
    {
        // 40% opaque white pixels, rest transparent.
        var buf = Glyph((0, 0, 0, 0), (255, 255, 255, 255), (int)(Size * Size * 0.4));
        Assert.Equal(IconTone.Light, IconToneClassifier.Classify(buf, Size, Size));
    }

    [Fact]
    public void BlackGlyph_OnTransparent_IsDark()
    {
        var buf = Glyph((0, 0, 0, 0), (0, 0, 0, 255), (int)(Size * Size * 0.4));
        Assert.Equal(IconTone.Dark, IconToneClassifier.Classify(buf, Size, Size));
    }

    [Fact]
    public void SaturatedColorMix_IsNormal()
    {
        // Half opaque orange, half opaque blue — visible but neither tone.
        var buf = Buffer(Size * Size, i =>
            i < Size * Size / 2
                ? ((byte)0, (byte)128, (byte)255, (byte)255)
                : ((byte)255, (byte)0, (byte)0, (byte)255));
        Assert.Equal(IconTone.Normal, IconToneClassifier.Classify(buf, Size, Size));
    }

    [Fact]
    public void WhiteTile_WithSmallBlackGlyph_IsNormal()
    {
        // 100% opaque white tile with a 25% black glyph — black is a minority.
        var buf = Glyph((255, 255, 255, 255), (0, 0, 0, 255), (int)(Size * Size * 0.25));
        Assert.Equal(IconTone.Normal, IconToneClassifier.Classify(buf, Size, Size));
    }

    [Fact]
    public void FullyTransparent_IsNormal()
        => Assert.Equal(IconTone.Normal,
            IconToneClassifier.Classify(Solid(0, 0, 0, 0), Size, Size));

    [Fact]
    public void LightGrayGlyph_IsLight()
    {
        // L ≈ 0.85 neutral gray still counts as light.
        var buf = Glyph((0, 0, 0, 0), (217, 217, 217, 255), (int)(Size * Size * 0.4));
        Assert.Equal(IconTone.Light, IconToneClassifier.Classify(buf, Size, Size));
    }

    [Fact]
    public void Premultiplied_HalfAlpha_WhiteGlyph_IsLight()
    {
        // Premultiplied half-alpha white: channels hold 128 = 255 * 128/255.
        var buf = Glyph((0, 0, 0, 0), (128, 128, 128, 128), (int)(Size * Size * 0.4));
        Assert.Equal(IconTone.Light, IconToneClassifier.Classify(buf, Size, Size));
    }
}
