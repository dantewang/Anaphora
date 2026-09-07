namespace Anaphora.Core;

/// <summary>Which way a bar runs.</summary>
public enum Axis
{
    Horizontal,
    Vertical,
}

/// <summary>Which end of a bar fills first.</summary>
public enum FillDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop,
}

/// <summary>A rectangle in capture-texture pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}@({X},{Y})";
}

/// <summary>
/// A rectangle expressed as fractions of the game's client area, which is how
/// every ROI is stored: the same profile then works at any resolution without
/// re-marking. Values are not clamped on construction -- an out-of-range
/// rectangle is a profile error worth reporting, not something to silently fix.
/// </summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    private const double Slack = 1e-6;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public bool IsValid =>
        Width > 0 && Height > 0 &&
        X >= -Slack && Y >= -Slack &&
        Right <= 1 + Slack && Bottom <= 1 + Slack;

    public static NormalizedRect FromPixels(int x, int y, int width, int height, int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);

        return new NormalizedRect(
            (double)x / frameWidth,
            (double)y / frameHeight,
            (double)width / frameWidth,
            (double)height / frameHeight);
    }

    /// <summary>
    /// Maps to pixels by rounding the edges rather than the size, so adjacent
    /// rectangles stay exactly adjacent instead of drifting apart by a pixel.
    /// </summary>
    public PixelRect ToPixels(int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);

        int left = Math.Clamp((int)Math.Round(X * frameWidth), 0, frameWidth);
        int top = Math.Clamp((int)Math.Round(Y * frameHeight), 0, frameHeight);
        int right = Math.Clamp((int)Math.Round(Right * frameWidth), 0, frameWidth);
        int bottom = Math.Clamp((int)Math.Round(Bottom * frameHeight), 0, frameHeight);

        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// One cell of an evenly divided bar. <paramref name="gapFraction"/> is the
    /// share of each cell treated as gutter and trimmed off both ends, which is
    /// how the skill-point segments avoid sampling their own dividers.
    /// </summary>
    public NormalizedRect Segment(int index, int count, Axis axis, double gapFraction = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        ArgumentOutOfRangeException.ThrowIfNegative(gapFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(gapFraction, 1);

        if (axis == Axis.Horizontal)
        {
            double cell = Width / count;
            double inset = cell * gapFraction / 2;
            return new NormalizedRect(X + (cell * index) + inset, Y, cell - (2 * inset), Height);
        }

        double row = Height / count;
        double rowInset = row * gapFraction / 2;
        return new NormalizedRect(X, Y + (row * index) + rowInset, Width, row - (2 * rowInset));
    }

    /// <summary>
    /// The part of this rectangle between two fractions along an axis.
    /// Slice(0.8, 1, Horizontal) is the right-hand fifth.
    /// </summary>
    public NormalizedRect Slice(double start, double end, Axis axis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, end);

        return axis == Axis.Horizontal
            ? new NormalizedRect(X + (Width * start), Y, Width * (end - start), Height)
            : new NormalizedRect(X, Y + (Height * start), Width, Height * (end - start));
    }

    /// <summary>Shrinks towards the centre by a fraction of each side.</summary>
    public NormalizedRect Inset(double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fraction, 1);

        double dx = Width * fraction / 2;
        double dy = Height * fraction / 2;
        return new NormalizedRect(X + dx, Y + dy, Width - (2 * dx), Height - (2 * dy));
    }

    /// <summary>Copy translated by whole cells along an axis, for repeated HUD slots.</summary>
    public NormalizedRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public override string ToString() =>
        $"({X:F4},{Y:F4}) {Width:F4}x{Height:F4}";
}
