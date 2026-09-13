using System.Text.Json.Serialization;

namespace Anaphora.Core;

/// <summary>
/// One thing to read out of a frame. Every ROI carries its own thresholds rather
/// than leaning on globals, because the same kind of element can need different
/// numbers in different corners of the same HUD.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SegmentedBarRoi), "segmentedBar")]
[JsonDerivedType(typeof(FillBarRoi), "fillBar")]
[JsonDerivedType(typeof(DiscStateRoi), "discState")]
[JsonDerivedType(typeof(PortraitSlotRoi), "portraitSlot")]
[JsonDerivedType(typeof(PresenceRoi), "presence")]
public abstract record RoiDefinition
{
    /// <summary>Stable key. Readings are published under this, so renaming it breaks bindings.</summary>
    public required string Id { get; init; }

    /// <summary>Human label for the marking UI and the overlay.</summary>
    public string? Label { get; init; }

    public required NormalizedRect Bounds { get; init; }
}

/// <summary>
/// A bar divided into discrete segments, read as a count of complete ones.
///
/// Endfield's skill points work this way: a filled segment is yellow, the one
/// currently charging is white, and everything past it is dark. Only whole
/// segments are counted -- nearly every skill costs a full one, so the partial
/// segment carries no decision.
/// </summary>
public sealed record SegmentedBarRoi : RoiDefinition
{
    public required int SegmentCount { get; init; }

    public Axis Axis { get; init; } = Axis.Horizontal;

    /// <summary>Share of each segment trimmed off both ends to clear the dividers.</summary>
    public double SegmentGap { get; init; } = 0.15;

    /// <summary>The colour a complete segment shows.</summary>
    public required ColourGate Filled { get; init; }

    /// <summary>
    /// The share of each segment, measured from the end it fills towards, that is
    /// actually sampled.
    ///
    /// Sampling the whole segment makes coverage proportional to how full it is,
    /// which puts "complete" and "nearly complete" a few percent apart and leaves
    /// no room for an effect sweeping past. Watching only the far end turns the
    /// same question into a crisp one: the last stretch is lit or it is not.
    /// Segments are assumed to fill along the axis in the positive direction.
    /// </summary>
    public double TailFraction { get; init; } = 0.2;

    /// <summary>Fraction of a segment's sampled pixels that must pass before it counts.</summary>
    public double FilledCoverage { get; init; } = 0.6;
}

/// <summary>
/// A continuously filling bar, read as a 0..1 ratio plus a ready flag.
///
/// Endfield's chain cooldowns: white across the full width means ready, and a
/// grey fill creeping in from the left means still recharging.
/// </summary>
public sealed record FillBarRoi : RoiDefinition
{
    public FillDirection Direction { get; init; } = FillDirection.LeftToRight;

    public required ColourGate Fill { get; init; }

    /// <summary>At or above this ratio the bar counts as ready.</summary>
    public double ReadyRatio { get; init; } = 0.97;
}

/// <summary>
/// A circular slot read by its rings: a charge arc that sweeps clockwise from
/// 12 o'clock, and an outer ring that lights up all the way round once the slot
/// is ready.
///
/// Endfield's ultimates. The first calibration judged the inner disc's mean
/// brightness, which worked on a dark map and failed on the first bright one:
/// the disc is translucent, and a sunlit floor seen through it is as bright as
/// a ready icon. What does not depend on the background is the two vivid rings.
/// Radii are fractions of half the bounds' shorter side, so the bounds must
/// enclose the outer ring.
/// </summary>
public sealed record DiscStateRoi : RoiDefinition
{
    /// <summary>Inner edge of the band the charge arc is drawn in.</summary>
    public double ArcInner { get; init; } = 0.36;

    /// <summary>Outer edge of the charge arc band.</summary>
    public double ArcOuter { get; init; } = 0.57;

    /// <summary>A pixel belongs to the arc at or above this luma...</summary>
    public double ArcLuma { get; init; } = 140;

    /// <summary>...and this saturation. The tinted background inside the disc stays well below both.</summary>
    public double ArcSaturation { get; init; } = 0.7;

    /// <summary>Inner edge of the ring that is lit only when the slot is ready.</summary>
    public double RingInner { get; init; } = 0.89;

    public double RingOuter { get; init; } = 0.98;

    public double RingLuma { get; init; } = 160;

    public double RingSaturation { get; init; } = 0.75;

    /// <summary>
    /// Share of the ring's circumference that must be lit to count as ready.
    /// Endfield's ready ring measures 86-89% -- it is not drawn quite all the way
    /// round -- against 0% for every charging slot across both captured fights,
    /// so the threshold sits in the middle of that gap, clear of a damage number
    /// hiding part of a ready ring or a background stripe crossing a charging one.
    /// </summary>
    public double ReadyCoverage { get; init; } = 0.6;
}

/// <summary>
/// A slot where a character portrait may appear, identified against the
/// profile's reference hashes.
///
/// Endfield's chain prompts sit at a fixed spot on screen. When several are
/// offered they line up left to right, and the leftmost -- <see cref="Priority"/>
/// zero -- is the one the key press actually triggers.
/// </summary>
public sealed record PortraitSlotRoi : RoiDefinition
{
    /// <summary>0 is the slot that fires first.</summary>
    public int Priority { get; init; }

    /// <summary>Fraction of the slot sampled, to stay inside the circular mask.</summary>
    public double InnerRadius { get; init; } = 0.80;

    /// <summary>Hamming distance beyond which a portrait match is rejected as "no one".</summary>
    public int MaxHashDistance { get; init; } = 12;
}

/// <summary>
/// Not a reading in its own right: a patch that says whether the HUD is on
/// screen at all. Every other ROI's output is meaningless unless this passes.
///
/// Two modes. With a <see cref="Signature"/>, the patch must be mostly that
/// colour -- the one Endfield uses, keyed on the left end of the player's HP
/// bar: an opaque, saturated cyan that is there whenever the HUD is, on any
/// background, and nowhere in cut-ins, menus or the reward screen.
///
/// Without one, the older contrast test applies: partly dark and partly not.
/// It was calibrated on a dark map and fails on a bright one, where the
/// translucent skill-point track shows the floor through it and loses its dark
/// half; it also passes ordinary text on a dark page. Kept for HUDs with no
/// opaque signature element.
/// </summary>
public sealed record PresenceRoi : RoiDefinition
{
    /// <summary>When set, presence means at least <see cref="MinimumCoverage"/> of the patch matches it.</summary>
    public ColourGate? Signature { get; init; }

    public double MinimumCoverage { get; init; } = 0.6;

    /// <summary>At or below this luma, 0..255, a pixel counts as background.</summary>
    public double DarkLuma { get; init; } = 40;

    /// <summary>Fraction of the patch that must be dark.</summary>
    public double MinimumDark { get; init; } = 0.08;

    /// <summary>
    /// Fraction of the patch that must not be dark. Deliberately low: with no
    /// skill points banked the only thing left lit is the segment borders, which
    /// measure around luma 63 -- present, but nowhere near a fill.
    /// </summary>
    public double MinimumLit { get; init; } = 0.10;
}
