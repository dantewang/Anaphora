using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>
/// A borrowed window onto BGRA pixels -- the layout WGC hands back, and the one
/// the ROI atlas will be read into. It owns nothing and copies nothing, so a
/// reader can run straight off a mapped staging texture.
/// </summary>
public readonly ref struct FrameView
{
    private readonly ReadOnlySpan<byte> pixels;

    public FrameView(ReadOnlySpan<byte> pixels, int width, int height)
        : this(pixels, width, height, width * 4)
    {
    }

    public FrameView(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);

        long needed = ((long)stride * (height - 1)) + ((long)width * 4);
        if (pixels.Length < needed)
        {
            throw new ArgumentException(
                $"{pixels.Length} bytes is short of the {needed} a {width}x{height} frame at stride {stride} needs.",
                nameof(pixels));
        }

        this.pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row. Never assume width * 4; the driver decides.</summary>
    public int Stride { get; }

    public Rgb this[int x, int y]
    {
        get
        {
            int p = (y * Stride) + (x * 4);
            return new Rgb(pixels[p + 2], pixels[p + 1], pixels[p]);
        }
    }

    /// <summary>Clamps a rectangle to the frame, so a slightly stale profile cannot walk off the edge.</summary>
    public PixelRect Clamp(PixelRect rect)
    {
        int x = Math.Clamp(rect.X, 0, Width);
        int y = Math.Clamp(rect.Y, 0, Height);
        return new PixelRect(x, y, Math.Clamp(rect.Width, 0, Width - x), Math.Clamp(rect.Height, 0, Height - y));
    }

    public PixelRect RectFor(NormalizedRect bounds) => Clamp(bounds.ToPixels(Width, Height));
}
