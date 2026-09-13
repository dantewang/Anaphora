using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Anaphora.Overlay;

/// <summary>
/// A decorator that paints its own outline: a parallelogram when <see cref="Slant"/>
/// is set, a rectangle with two opposite corners cut at 45° when
/// <see cref="Chamfer"/> is. Endfield's HUD is built almost entirely from these
/// two shapes, and a Border cannot draw either.
/// </summary>
public sealed class ShapeDecorator : Decorator
{
    public static readonly StyledProperty<double> SlantProperty =
        AvaloniaProperty.Register<ShapeDecorator, double>(nameof(Slant));

    public static readonly StyledProperty<double> ChamferProperty =
        AvaloniaProperty.Register<ShapeDecorator, double>(nameof(Chamfer));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ShapeDecorator, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ShapeDecorator, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ShapeDecorator, double>(nameof(StrokeThickness), 1);

    static ShapeDecorator()
    {
        AffectsRender<ShapeDecorator>(SlantProperty, ChamferProperty, FillProperty, StrokeProperty, StrokeThicknessProperty);
    }

    /// <summary>Horizontal offset of the top edge against the bottom edge, in DIPs.</summary>
    public double Slant
    {
        get => GetValue(SlantProperty);
        set => SetValue(SlantProperty, value);
    }

    /// <summary>Size of the cut on the top-right and bottom-left corners, in DIPs.</summary>
    public double Chamfer
    {
        get => GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
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

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // Keep the stroke inside the bounds.
        double inset = Stroke is null ? 0 : StrokeThickness / 2;
        Point[] points = Slant > 0
            ?
            [
                new(Slant + inset, inset),
                new(w - inset, inset),
                new(w - Slant - inset, h - inset),
                new(inset, h - inset),
            ]
            :
            [
                new(inset, inset),
                new(w - Chamfer - inset, inset),
                new(w - inset, Chamfer + inset),
                new(w - inset, h - inset),
                new(Chamfer + inset, h - inset),
                new(inset, h - Chamfer - inset),
            ];

        var geometry = new StreamGeometry();
        using (StreamGeometryContext path = geometry.Open())
        {
            path.BeginFigure(points[0], isFilled: true);
            foreach (Point point in points.AsSpan(1))
            {
                path.LineTo(point);
            }

            path.EndFigure(isClosed: true);
        }

        context.DrawGeometry(Fill, Stroke is null ? null : new Pen(Stroke, StrokeThickness), geometry);
    }
}
