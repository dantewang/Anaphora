namespace Anaphora.Core;

/// <summary>How to recognise the game window.</summary>
public sealed record WindowMatch
{
    public required string ProcessName { get; init; }

    /// <summary>Win32 class name, checked when present. Endfield reports UnityWndClass.</summary>
    public string? WindowClass { get; init; }
}

/// <summary>
/// A known character, stored as a perceptual hash of their portrait so chain
/// prompts can be attributed to someone. Hashes are captured from the team
/// panel, which shows the same art the prompt does.
/// </summary>
public sealed record PortraitReference
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>64-bit dHash: an 8x9 grey grid compared column-wise.</summary>
    public required ulong Hash { get; init; }
}

/// <summary>
/// Everything needed to read one game's HUD. Written by the marking UI, read by
/// the capture pipeline; nothing in here knows about Windows or rendering.
/// </summary>
public sealed record GameProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Free-text notes: what was measured, what is still provisional.</summary>
    public string? Notes { get; init; }

    public required WindowMatch Window { get; init; }

    /// <summary>
    /// Id of the <see cref="PresenceRoi"/> that gates every other reading. Without
    /// one the pipeline will happily report thresholds crossed by scenery.
    /// </summary>
    public string? HudSentinelRoiId { get; init; }

    public IReadOnlyList<RoiDefinition> Rois { get; init; } = [];

    public IReadOnlyList<PortraitReference> Portraits { get; init; } = [];

    public RoiDefinition? FindRoi(string id) =>
        Rois.FirstOrDefault(roi => string.Equals(roi.Id, id, StringComparison.Ordinal));

    public IEnumerable<T> RoisOf<T>()
        where T : RoiDefinition => Rois.OfType<T>();

    /// <summary>
    /// Compares the lists by content. The compiler-generated version would use
    /// reference equality on them, so two profiles differing only in their ROIs
    /// would report equal -- which would quietly break any "has this been
    /// edited?" check in the marking UI.
    /// </summary>
    public bool Equals(GameProfile? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion &&
        Id == other.Id &&
        DisplayName == other.DisplayName &&
        Notes == other.Notes &&
        Window == other.Window &&
        HudSentinelRoiId == other.HudSentinelRoiId &&
        Rois.SequenceEqual(other.Rois) &&
        Portraits.SequenceEqual(other.Portraits);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(SchemaVersion);
        hash.Add(Id);
        hash.Add(DisplayName);
        hash.Add(Notes);
        hash.Add(Window);
        hash.Add(HudSentinelRoiId);

        foreach (RoiDefinition roi in Rois)
        {
            hash.Add(roi);
        }

        foreach (PortraitReference portrait in Portraits)
        {
            hash.Add(portrait);
        }

        return hash.ToHashCode();
    }
}
