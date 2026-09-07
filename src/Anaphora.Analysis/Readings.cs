namespace Anaphora.Analysis;

/// <summary>Whether the HUD is on screen, with the evidence that decided it.</summary>
public readonly record struct PresenceReading(bool IsPresent, double DarkFraction, double LitFraction)
{
    public override string ToString() =>
        $"{(IsPresent ? "present" : "absent")} (dark {DarkFraction:P1}, lit {LitFraction:P1})";
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

/// <summary>Whether a circular slot holds an icon.</summary>
public readonly record struct DiscStateReading(bool IsReady, double MeanLuma, double MeanSaturation)
{
    public override string ToString() =>
        $"{(IsReady ? "ready" : "charging")} (luma {MeanLuma:F0}, sat {MeanSaturation:F2})";
}

/// <summary>Who, if anyone, is showing in a portrait slot.</summary>
/// <param name="PortraitId">Null when nothing matched closely enough.</param>
public readonly record struct PortraitReading(string? PortraitId, int Distance, ulong Hash)
{
    public bool IsPresent => PortraitId is not null;

    public override string ToString() =>
        PortraitId is null ? $"unknown (best distance {Distance})" : $"{PortraitId} (distance {Distance})";
}
