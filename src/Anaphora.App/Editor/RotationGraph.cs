using System.Globalization;
using Anaphora.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Anaphora.App.Editor;

/// <summary>
/// Draws a rotation as lanes of step cards -- one lane per segment -- with the
/// soft cues stacked above each card, its wait conditions below, and arrows for
/// where each step goes. Drawn by hand rather than composed from controls: the
/// arrows need every card's position, and a custom control is the simplest way
/// to have both in one place. Clicking a card selects it.
/// </summary>
internal sealed class RotationGraph : Control
{
    public static readonly StyledProperty<RotationDocument?> DocumentProperty =
        AvaloniaProperty.Register<RotationGraph, RotationDocument?>(nameof(Document));

    public static readonly StyledProperty<StepModel?> SelectedStepProperty =
        AvaloniaProperty.Register<RotationGraph, StepModel?>(nameof(SelectedStep), defaultBindingMode: BindingMode.TwoWay);

    private const double Pad = 24;
    private const double LabelWidth = 92;
    private const double CardWidth = 118;
    private const double CardHeight = 64;
    private const double ColumnGap = 44;
    private const double ChipHeight = 20;
    private const double ChipGap = 4;
    private const double LaneGap = 52;
    private const double Chamfer = 10;

    private static readonly FontFamily Display = new("Bahnschrift SemiCondensed, Bahnschrift, Microsoft YaHei UI");
    private static readonly FontFamily Body = new("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static readonly IBrush Text = Brush("#ECEBE6");
    private static readonly IBrush Muted = Brush("#8A8E8F");
    private static readonly IBrush Dim = Brush("#5E6263");
    private static readonly IBrush Card = Brush("#181B1B");
    private static readonly IBrush Line = Brush("#34383A");
    private static readonly IBrush Amber = Brush("#F0B429");
    private static readonly IBrush AmberDeep = Brush("#3A2C0B");
    private static readonly IBrush Cyan = Brush("#19C3F0");
    private static readonly IBrush Lime = Brush("#B8D433");
    private static readonly IBrush LimeWash = Brush("#0FB8D433");
    private static readonly IBrush Ground = Brush("#0A0B0B");
    private static readonly IBrush Dots = Brush("#1D2020");

    private readonly Dictionary<StepModel, Rect> cards = [];
    private readonly List<Lane> lanes = [];
    private RotationDocument? watched;
    private Size extent;

    static RotationGraph()
    {
        AffectsMeasure<RotationGraph>(DocumentProperty);
        AffectsRender<RotationGraph>(SelectedStepProperty);
        FocusableProperty.OverrideDefaultValue<RotationGraph>(true);
    }

    public RotationDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public StepModel? SelectedStep
    {
        get => GetValue(SelectedStepProperty);
        set => SetValue(SelectedStepProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            if (watched is not null)
            {
                watched.Changed -= OnDocumentChanged;
            }

            watched = Document;
            if (watched is not null)
            {
                watched.Changed += OnDocumentChanged;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Layout();
        return extent;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point at = e.GetPosition(this);
        foreach ((StepModel step, Rect rect) in cards)
        {
            if (rect.Inflate(new Thickness(0, Stack(step.Cues.Count), 0, Stack(step.Waits.Count))).Contains(at))
            {
                SelectedStep = step;
                Focus();
                return;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        // A dotted ground, like the mockup's canvas.
        context.FillRectangle(Ground, new Rect(Bounds.Size));
        for (double y = 11; y < Bounds.Height; y += 22)
        {
            for (double x = 11; x < Bounds.Width; x += 22)
            {
                context.FillRectangle(Dots, new Rect(x, y, 1.2, 1.2));
            }
        }

        if (Document is not { } document)
        {
            return;
        }

        foreach (Lane lane in lanes)
        {
            Draw(context, lane.Segment.Name, Display, 15, FontWeight.SemiBold, Text, new Point(Pad, lane.CardTop + 10));
            Draw(context, string.Create(CultureInfo.InvariantCulture, $"{lane.Count} 步"), Body, 11.5, FontWeight.Normal, Muted, new Point(Pad, lane.CardTop + 32));
        }

        DrawEdges(context, document);

        foreach ((StepModel step, Rect rect) in cards)
        {
            DrawCard(context, document, step, rect);
        }
    }

    private static IBrush Brush(string colour) => new ImmutableSolidColorBrush(Color.Parse(colour));

    private static double Stack(int count) => count == 0 ? 0 : count * (ChipHeight + ChipGap) + 2;

    private void OnDocumentChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Places every card. A lane starts one column to the right of a branch
    /// leading into it from a lane above, so it sits beside the step it
    /// leaves from.
    /// </summary>
    private void Layout()
    {
        cards.Clear();
        lanes.Clear();
        if (Document is not { } document)
        {
            extent = default;
            return;
        }

        var columns = new Dictionary<StepModel, int>();
        double top = Pad;
        double right = 0;

        foreach (SegmentModel segment in document.Segments)
        {
            List<StepModel> steps = [.. document.StepsIn(segment)];
            if (steps.Count == 0)
            {
                continue;
            }

            int offset = 0;
            foreach ((StepModel placed, int column) in columns)
            {
                bool leadsIn = placed.Branches.Any(b => b.To == steps[0].Id);
                if (leadsIn && placed != steps[0])
                {
                    offset = Math.Max(offset, column + 1);
                }
            }

            double cueZone = steps.Max(s => Stack(s.Cues.Count));
            double waitZone = steps.Max(s => Stack(s.Waits.Count));
            double cardTop = top + cueZone;

            for (int i = 0; i < steps.Count; i++)
            {
                int column = offset + i;
                columns[steps[i]] = column;
                var rect = new Rect(Pad + LabelWidth + (column * (CardWidth + ColumnGap)), cardTop, CardWidth, CardHeight);
                cards[steps[i]] = rect;
                right = Math.Max(right, rect.Right);
            }

            double bottom = cardTop + CardHeight + waitZone;
            lanes.Add(new Lane(segment, steps.Count, top, cardTop, bottom));
            top = bottom + LaneGap;
        }

        extent = new Size(right + Pad + 40, top - LaneGap + Pad + 30);
    }

    private void DrawCard(DrawingContext context, RotationDocument document, StepModel step, Rect rect)
    {
        bool selected = step == SelectedStep;
        var outline = new StreamGeometry();
        using (StreamGeometryContext path = outline.Open())
        {
            path.BeginFigure(rect.TopLeft, true);
            path.LineTo(new Point(rect.Right - Chamfer, rect.Top));
            path.LineTo(new Point(rect.Right, rect.Top + Chamfer));
            path.LineTo(rect.BottomRight);
            path.LineTo(rect.BottomLeft);
            path.EndFigure(true);
        }

        context.DrawGeometry(Card, new Pen(selected ? Amber : Line, selected ? 2 : 1), outline);
        if (document.Steps.Count > 0 && document.Steps[0] == step)
        {
            context.FillRectangle(Amber, new Rect(rect.Left, rect.Top, 18, 2));
        }

        int position = document.StepsIn(document.SegmentOf(step)).TakeWhile(s => s != step).Count() + 1;
        Draw(context, position.ToString("00", CultureInfo.InvariantCulture), Mono, 10, FontWeight.Normal, Dim, new Point(rect.Left + 8, rect.Top + 6));

        IBrush tone = step.Kind switch
        {
            ActionKind.Chain => Cyan,
            ActionKind.Ultimate => Amber,
            _ => Text,
        };
        // "3大招" is twice as wide as "3": keep it off the line below.
        string action = step.Action.ToString();
        Draw(context, action, Display, action.Length > 2 ? 22 : 28, FontWeight.Bold, tone, new Point(rect.Left + 8, rect.Top + (action.Length > 2 ? 17 : 14)));

        string kind = step.Kind switch
        {
            ActionKind.Chain => "连携",
            ActionKind.Ultimate => "大招",
            _ => "技能",
        };
        Draw(context, $"{kind}  {step.Action.KeyText}", Body, 11, FontWeight.Normal, Muted, new Point(rect.Left + 8, rect.Bottom - 18));

        // Cues stack upwards from the card, first cue on top.
        for (int i = 0; i < step.Cues.Count; i++)
        {
            double y = rect.Top - ((step.Cues.Count - i) * (ChipHeight + ChipGap));
            var chip = new Rect(rect.Left, y, rect.Width, ChipHeight);
            context.DrawRectangle(LimeWash, new Pen(Lime, 1, new DashStyle([3, 2], 0)), chip);
            DrawCentred(context, Describe(step.Cues[i]), Body, 11.5, Lime, chip);
        }

        for (int i = 0; i < step.Waits.Count; i++)
        {
            var chip = new Rect(rect.Left, rect.Bottom + ChipGap + (i * (ChipHeight + ChipGap)), rect.Width, ChipHeight);
            context.DrawRectangle(AmberDeep, new Pen(Amber, 1), chip);
            DrawCentred(context, $"等待 {step.Waits[i].ToCondition().Description}", Body, 11, Amber, chip);
        }
    }

    private static string Describe(CueModel cue) => cue.Kind switch
    {
        CueKind.Heavy => $"{cue.Slot} 重击",
        CueKind.AttackAfterSkill => $"{cue.Slot} 技能后第 {cue.Count} 下",
        _ => cue.Text,
    };

    private void DrawEdges(DrawingContext context, RotationDocument document)
    {
        var plain = new Pen(Dim, 1.2);
        var loop = new Pen(Amber, 1.2, new DashStyle([4, 3], 0));
        var branchPen = new Pen(Cyan, 1.2);
        var gutters = new Dictionary<Lane, int>();

        foreach (StepModel step in document.Steps)
        {
            if (!cards.TryGetValue(step, out Rect from))
            {
                continue;
            }

            (StepModel next, bool newRound) = document.DefaultNext(step);
            if (cards.TryGetValue(next, out Rect to))
            {
                if (newRound || !IsForward(from, to))
                {
                    string label = newRound ? $"下一轮 · 回到 {document.Label(next)}" : $"回到 {document.Label(next)}";
                    Around(step, from, next, to, newRound ? loop : plain, label, newRound ? Amber : Muted);
                }
                else
                {
                    Forward(from, to, plain, null, null);
                }
            }

            foreach (BranchModel branch in step.Branches)
            {
                if (document.Find(branch.To) is { } target && cards.TryGetValue(target, out Rect branchTo))
                {
                    string label = $"若 {branch.Condition.ToCondition().Description}";
                    if (branchTo.Left > from.Left)
                    {
                        Forward(from, branchTo, branchPen, label, Cyan, fromBottom: true);
                    }
                    else
                    {
                        Around(step, from, target, branchTo, branchPen, label, Cyan);
                    }
                }
            }
        }

        void Forward(Rect a, Rect b, IPen pen, string? label, IBrush? labelBrush, bool fromBottom = false)
        {
            var points = new List<Point>();
            if (Math.Abs(a.Center.Y - b.Center.Y) < 1 && !fromBottom)
            {
                points.Add(new Point(a.Right, a.Center.Y));
            }
            else if (fromBottom)
            {
                // Down out of the card, across to the gap before the target, in.
                double x = a.Right - 16;
                points.Add(new Point(x, a.Bottom));
                points.Add(new Point(x, b.Center.Y));
                if (label is not null)
                {
                    Label(label, new Point(x, (a.Bottom + b.Center.Y) / 2), labelBrush!);
                }
            }
            else
            {
                double x = b.Left - (ColumnGap / 2);
                points.Add(new Point(a.Right, a.Center.Y));
                points.Add(new Point(x, a.Center.Y));
                points.Add(new Point(x, b.Center.Y));
            }

            points.Add(new Point(b.Left - 1, b.Center.Y));
            Polyline(points, pen);
        }

        // Out of the bottom, along the gutter under the lane, and into the target.
        void Around(StepModel step, Rect a, StepModel target, Rect b, IPen pen, string label, IBrush labelBrush)
        {
            Lane lane = lanes.First(l => a.Top >= l.CardTop && a.Top <= l.Bottom);
            gutters.TryGetValue(lane, out int used);
            gutters[lane] = used + 1;
            double gutter = lane.Bottom + 12 + (used * 12);
            double startX = a.Center.X + 6;

            bool below = b.Top > a.Bottom;
            double endY = below ? b.Top - Stack(target.Cues.Count) : b.Bottom + Stack(target.Waits.Count);
            double endX = b.Center.X - 6 + (used * 6);

            Polyline(
            [
                new Point(startX, a.Bottom + Stack(step.Waits.Count)),
                new Point(startX, gutter),
                new Point(endX, gutter),
                new Point(endX, endY + (below ? -1 : 1)),
            ], pen);
            Label(label, new Point((startX + endX) / 2, gutter), labelBrush);
        }

        void Polyline(IReadOnlyList<Point> points, IPen pen)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(points[0], false);
                foreach (Point point in points.Skip(1))
                {
                    path.LineTo(point);
                }

                path.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
            Arrowhead(points[^2], points[^1], pen.Brush!);
        }

        void Arrowhead(Point from, Point tip, IBrush brush)
        {
            Vector direction = tip - from;
            double length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
            if (length < 0.001)
            {
                return;
            }

            Vector unit = direction / length;
            var normal = new Vector(-unit.Y, unit.X);
            var head = new StreamGeometry();
            using (StreamGeometryContext path = head.Open())
            {
                path.BeginFigure(tip, true);
                path.LineTo(tip - (unit * 7) + (normal * 3.5));
                path.LineTo(tip - (unit * 7) - (normal * 3.5));
                path.EndFigure(true);
            }

            context.DrawGeometry(brush, null, head);
        }

        void Label(string text, Point centre, IBrush brush)
        {
            FormattedText formatted = Format(text, Body, 11, FontWeight.Normal, brush);
            var box = new Rect(centre.X - (formatted.Width / 2) - 4, centre.Y - (formatted.Height / 2), formatted.Width + 8, formatted.Height);
            context.FillRectangle(Ground, box);
            context.DrawText(formatted, new Point(box.Left + 4, box.Top));
        }
    }

    private static bool IsForward(Rect from, Rect to) => to.Left > from.Left;

    private static FormattedText Format(string text, FontFamily family, double size, FontWeight weight, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);

    private static void Draw(DrawingContext context, string text, FontFamily family, double size, FontWeight weight, IBrush brush, Point at) =>
        context.DrawText(Format(text, family, size, weight, brush), at);

    private static void DrawCentred(DrawingContext context, string text, FontFamily family, double size, IBrush brush, Rect box)
    {
        FormattedText formatted = Format(text, family, size, FontWeight.Normal, brush);
        formatted.MaxTextWidth = box.Width - 6;
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        context.DrawText(formatted, new Point(box.Left + ((box.Width - formatted.Width) / 2), box.Top + ((box.Height - formatted.Height) / 2)));
    }

    private sealed record Lane(SegmentModel Segment, int Count, double Top, double CardTop, double Bottom);
}
