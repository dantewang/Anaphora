using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>
/// A borrowed window onto BGRA pixels -- the layout WGC hands back, and the one
/// the ROI atlas is read into. It owns nothing and copies nothing, so a reader
/// can run straight off a mapped staging texture.
///
/// Coordinates are always in the game's client area. The bytes underneath may
/// be a whole capture texture, a texture with window chrome around the client
/// area, or a small atlas holding only the ROIs; an origin offset hides which,
/// so the readers never need to know.
/// </summary>
public readonly ref struct FrameView
{
    private readonly ReadOnlySpan<byte> pixels;
    private readonly int originX;
    private readonly int originY;

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

    /// <summary>
    /// Unchecked: a reframed view may be logically far larger than its bytes, as
    /// when a 3840x2160 client area is served out of a 700x300 atlas. Only the
    /// ROI rectangles are guaranteed to be backed; the span's own bounds check is
    /// what stops anything else from reading past the end.
    /// </summary>
    private FrameView(ReadOnlySpan<byte> pixels, int width, int height, int stride, int originX, int originY)
    {
        this.pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        this.originX = originX;
        this.originY = originY;
    }

    /// <summary>Logical width: the client area, whatever the bytes underneath are.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row of the underlying buffer. Never assume width * 4; the driver decides.</summary>
    public int Stride { get; }

    public Rgb this[int x, int y]
    {
        get
        {
            int p = ((y + originY) * Stride) + ((x + originX) * 4);
            return new Rgb(pixels[p + 2], pixels[p + 1], pixels[p]);
        }
    }

    /// <summary>
    /// The client area inside a whole capture texture. Windowed games are
    /// captured with their title bar and borders; this puts (0,0) back at the
    /// top-left of the client area so normalised ROI bounds line up.
    /// </summary>
    public FrameView ClientArea(PixelRect area)
    {
        if (area.IsEmpty || area.X < 0 || area.Y < 0 || area.Right > Width || area.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(area), $"{area} does not fit a {Width}x{Height} frame.");
        }

        return new FrameView(pixels, area.Width, area.Height, Stride, originX + area.X, originY + area.Y);
    }

    /// <summary>
    /// The same bytes seen through a different coordinate system: logical
    /// (x, y) reads buffer (x + originX, y + originY). For <see cref="RoiAtlas"/>.
    /// </summary>
    internal FrameView Reframe(int width, int height, int newOriginX, int newOriginY) =>
        new(pixels, width, height, Stride, newOriginX, newOriginY);

    /// <summary>Clamps a rectangle to the frame, so a slightly stale profile cannot walk off the edge.</summary>
    public PixelRect Clamp(PixelRect rect)
    {
        int x = Math.Clamp(rect.X, 0, Width);
        int y = Math.Clamp(rect.Y, 0, Height);
        return new PixelRect(x, y, Math.Clamp(rect.Width, 0, Width - x), Math.Clamp(rect.Height, 0, Height - y));
    }

    public PixelRect RectFor(NormalizedRect bounds) => Clamp(bounds.ToPixels(Width, Height));
}
