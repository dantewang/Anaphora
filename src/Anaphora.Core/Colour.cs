using System.Globalization;

namespace Anaphora.Core;

/// <summary>An 8-bit colour. Serialised as "#RRGGBB" so profiles stay editable by hand.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Rec. 709 luma, 0..255.</summary>
    public double Luma => (R * 0.2126) + (G * 0.7152) + (B * 0.0722);

    /// <summary>HSV saturation, 0..1. Zero for any shade of grey.</summary>
    public double Saturation
    {
        get
        {
            int max = Math.Max(R, Math.Max(G, B));
            int min = Math.Min(R, Math.Min(G, B));
            return max == 0 ? 0 : (double)(max - min) / max;
        }
    }

    public static Rgb Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length != 6 ||
            !byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            throw new FormatException($"'{text}' is not a #RRGGBB colour.");
        }

        return new Rgb(r, g, b);
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Accepts or rejects a pixel by distance from a target colour.
///
/// Plain Euclidean distance in RGB is crude -- it does not know that a bar lit
/// by a passing effect is still the same bar -- but it is predictable and easy
/// to calibrate against captured frames, which matters more at this stage than
/// being perceptually correct. Anything smarter belongs in Anaphora.Analysis,
/// behind the same interface.
/// </summary>
public readonly record struct ColourGate(Rgb Target, double Tolerance)
{
    /// <summary>Distance between opposite corners of the RGB cube.</summary>
    private const double MaxDistance = 441.6729559300637;

    public bool Accepts(byte r, byte g, byte b)
    {
        double dr = r - Target.R;
        double dg = g - Target.G;
        double db = b - Target.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db)) <= Tolerance * MaxDistance;
    }

    public bool Accepts(Rgb colour) => Accepts(colour.R, colour.G, colour.B);

    public bool IsValid => Tolerance is > 0 and <= 1;
}
