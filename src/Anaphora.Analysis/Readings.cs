namespace Anaphora.Analysis;

/// <summary>
/// Whether the HUD is on screen, with the evidence that decided it: the share
/// of the patch matching the signature colour, or for a contrast sentinel the
/// dark and non-dark shares.
/// </summary>
public readonly record struct PresenceReading(bool IsPresent, double Coverage, double DarkFraction, double LitFraction)
{
    public override string ToString() => Coverage > 0 || (DarkFraction == 0 && LitFraction == 0)
        ? $"{(IsPresent ? "present" : "absent")} (signature {Coverage:P0})"
        : $"{(IsPresent ? "present" : "absent")} (dark {DarkFraction:P1}, lit {LitFraction:P1})";
}

/// <summary>How many whole segments of a segmented bar are filled.</summary>
public readonly record struct SegmentedBarReading(int Filled, int SegmentCount)
{
    public override string ToString() => $"{Filled}/{SegmentCount}";
}

/// <summary>How far a continuous bar has filled, and whether that counts as ready.</summary>
public readonly record struct FillBarReading(double Ratio, bool IsReady)
{
    public override string ToString() => $"{Ratio:P0}{(IsReady ? " ready" : string.Empty)}";
}

/// <summary>Whether a circular slot is ready, and how far its charge arc has swept.</summary>
/// <param name="Charge">0..1 clockwise from 12 o'clock; 1 when ready.</param>
/// <param name="RingCoverage">Share of the ready ring that is lit; the evidence for <paramref name="IsReady"/>.</param>
public readonly record struct DiscStateReading(bool IsReady, double Charge, double RingCoverage)
{
    public override string ToString() =>
        IsReady ? $"ready (ring {RingCoverage:P0})" : $"charging {Charge:P0} (ring {RingCoverage:P0})";
}

/// <summary>Who, if anyone, is showing in a portrait slot.</summary>
/// <param name="PortraitId">Null when nothing matched closely enough.</param>
public readonly record struct PortraitReading(string? PortraitId, int Distance, ulong Hash)
{
    /// <summary>The matched portrait's team slot, when it has one.</summary>
    public int? Slot { get; init; }

    public bool IsPresent => PortraitId is not null;

    public override string ToString() =>
        PortraitId is null ? $"unknown (best distance {Distance})" : $"{PortraitId} (distance {Distance})";
}
