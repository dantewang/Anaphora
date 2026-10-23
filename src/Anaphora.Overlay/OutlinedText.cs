using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Anaphora.Overlay;

/// <summary>
/// Text with a real outline: the glyph outlines are stroked first, then filled,
/// so the stroke sits behind the fill and the letterform keeps its shape.
///
/// A TextBlock cannot do this -- Avalonia has no equivalent of CSS
/// text-stroke -- and stacking offset copies thickens the glyph instead of
/// ringing it. White text over a game that is sometimes a dark cave and
/// sometimes a sunlit field needs the ring.
///
/// Letter spacing is applied by laying the text out one text element at a time,
/// which is why a string with spacing is not shaped as a whole. These are short
/// labels and digits, so nothing is lost; a zero spacing takes the normal path.
///
/// The control measures the glyphs' ink, not the font's line box, so it sits
/// tight against whatever is above and below it.
/// </summary>
public sealed class OutlinedText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OutlinedText, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<OutlinedText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<OutlinedText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<OutlinedText>();

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<OutlinedText, IBrush?>(nameof(Fill), Brushes.White);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<OutlinedText, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<OutlinedText, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<double> LetterSpacingProperty =
        AvaloniaProperty.Register<OutlinedText, double>(nameof(LetterSpacing));

    private Geometry? glyphs;
    private Size drawn;

    static OutlinedText()
    {
        AffectsMeasure<OutlinedText>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty,
            StrokeThicknessProperty, LetterSpacingProperty);
        AffectsRender<OutlinedText>(FillProperty, StrokeProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Total width of the ring; half of it falls outside the letterform.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>Extra advance after each text element, in DIPs.</summary>
    public double LetterSpacing
    {
        get => GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (glyphs is null)
        {
            return;
        }

        // Stroke first, fill over it: the ring grows outwards only, so the
        // letterform stays the shape the type designer drew.
        if (Stroke is { } stroke && StrokeThickness > 0)
        {
            context.DrawGeometry(null, new Pen(stroke, StrokeThickness, lineJoin: PenLineJoin.Round), glyphs);
        }

        context.DrawGeometry(Fill, null, glyphs);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();
        return drawn;
    }

    private void Build()
    {
        glyphs = null;
        drawn = default;

        if (string.IsNullOrEmpty(Text) || FontSize <= 0)
        {
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        Geometry? built;

        if (LetterSpacing == 0)
        {
            built = Format(Text, typeface).BuildGeometry(default);
        }
        else
        {
            var group = new GeometryGroup();
            double x = 0;
            var walker = StringInfo.GetTextElementEnumerator(Text);
            while (walker.MoveNext())
            {
                FormattedText element = Format((string)walker.Current, typeface);
                if (element.BuildGeometry(new Point(x, 0)) is { } geometry)
                {
                    group.Children.Add(geometry);
                }

                x += element.WidthIncludingTrailingWhitespace + LetterSpacing;
            }

            built = group;
        }

        Rect ink = built?.Bounds ?? default;
        if (built is null || ink.Width <= 0 || ink.Height <= 0)
        {
            return;
        }

        // Measure the ink, not the line box. A font's line box carries leading
        // that varies with which metrics the text stack happens to use -- for
        // Arial Black that is 1.07 em here and 1.41 em in a TextBlock -- so a
        // layout tuned by eye against one of them clips the glyph under the
        // other. The ink bounds are the glyph and nothing else, which also means
        // the card needs no negative margins to look tight.
        double pad = Math.Max(StrokeThickness, 0) / 2;
        built.Transform = new TranslateTransform(pad - ink.X, pad - ink.Y);
        glyphs = built;
        drawn = new Size(ink.Width + (pad * 2), ink.Height + (pad * 2));
    }

    // The brush is only there because FormattedText wants one; the geometry it
    // builds carries no colour, and Render picks the brushes.
    private FormattedText Format(string text, Typeface typeface) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.White);
}
