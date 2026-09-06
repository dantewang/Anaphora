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
/// A circular slot that either holds an icon or sits empty, read as a boolean.
///
/// Endfield's ultimates: a coloured icon inside the small circle means castable,
/// an empty dark disc with an arc on its ring means still charging. Only the
/// inner disc is sampled so the ring's charge arc cannot sway the verdict.
/// </summary>
public sealed record DiscStateRoi : RoiDefinition
{
    /// <summary>Fraction of the inscribed circle sampled, measured as a radius.</summary>
    public double InnerRadius { get; init; } = 0.62;

    /// <summary>Mean luma, 0..255, at or above which the slot counts as filled.</summary>
    public double ReadyLuma { get; init; } = 60;

    /// <summary>Mean saturation, 0..1, required alongside the luma test.</summary>
    public double ReadySaturation { get; init; } = 0.20;
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
/// screen at all.
///
/// The frame is unreadable in three situations -- out of combat, during a
/// full-screen ultimate cut-in, and behind a chain cut-in -- and in all three
/// the HUD is absent rather than dimmed. Every other ROI's output is meaningless
/// unless this one passes.
/// </summary>
public sealed record PresenceRoi : RoiDefinition
{
    public required ColourGate Signature { get; init; }

    /// <summary>Fraction of sampled pixels that must match before the HUD counts as present.</summary>
    public double MinimumCoverage { get; init; } = 0.02;
}
