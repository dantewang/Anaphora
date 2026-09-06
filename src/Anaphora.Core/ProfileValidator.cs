namespace Anaphora.Core;

public enum ProfileProblemSeverity
{
    /// <summary>The profile is usable, but something is likely to bite later.</summary>
    Warning,

    /// <summary>The profile cannot be trusted to produce meaningful readings.</summary>
    Error,
}

public sealed record ProfileProblem(ProfileProblemSeverity Severity, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>
/// Checks a profile for the mistakes that are easy to make by hand and hard to
/// spot in a running overlay -- a ROI nudged off the edge, a duplicated id, a
/// sentinel pointing at nothing. Reports everything it finds rather than
/// throwing on the first, so the marking UI can list the lot.
/// </summary>
public static class ProfileValidator
{
    public static IReadOnlyList<ProfileProblem> Validate(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var problems = new List<ProfileProblem>();

        void Error(string message) => problems.Add(new ProfileProblem(ProfileProblemSeverity.Error, message));
        void Warn(string message) => problems.Add(new ProfileProblem(ProfileProblemSeverity.Warning, message));

        if (profile.SchemaVersion != GameProfile.CurrentSchemaVersion)
        {
            Error($"schema version {profile.SchemaVersion} is not the current {GameProfile.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            Error("profile id is empty.");
        }

        if (string.IsNullOrWhiteSpace(profile.Window.ProcessName))
        {
            Error("window.processName is empty; nothing can be matched.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RoiDefinition roi in profile.Rois)
        {
            string where = $"roi '{roi.Id}'";

            if (string.IsNullOrWhiteSpace(roi.Id))
            {
                Error("a roi has an empty id.");
            }
            else if (!seen.Add(roi.Id))
            {
                Error($"{where}: duplicate id.");
            }

            if (!roi.Bounds.IsValid)
            {
                Error($"{where}: bounds {roi.Bounds} are empty or fall outside the frame.");
            }

            foreach (string message in ValidateSpecific(roi, where))
            {
                Error(message);
            }
        }

        if (profile.HudSentinelRoiId is { } sentinelId)
        {
            RoiDefinition? sentinel = profile.FindRoi(sentinelId);
            if (sentinel is null)
            {
                Error($"hudSentinelRoiId '{sentinelId}' does not match any roi.");
            }
            else if (sentinel is not PresenceRoi)
            {
                Error($"hudSentinelRoiId '{sentinelId}' points at a {sentinel.GetType().Name}, not a PresenceRoi.");
            }
        }
        else
        {
            Warn("no hudSentinelRoiId: readings would be reported even with the HUD off screen.");
        }

        var priorities = new HashSet<int>();
        foreach (PortraitSlotRoi slot in profile.RoisOf<PortraitSlotRoi>())
        {
            if (!priorities.Add(slot.Priority))
            {
                Error($"roi '{slot.Id}': priority {slot.Priority} is used by another portrait slot.");
            }
        }

        var portraitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (PortraitReference portrait in profile.Portraits)
        {
            if (!portraitIds.Add(portrait.Id))
            {
                Error($"portrait '{portrait.Id}': duplicate id.");
            }
        }

        if (profile.RoisOf<PortraitSlotRoi>().Any() && profile.Portraits.Count == 0)
        {
            Warn("portrait slots are defined but no reference portraits exist yet, so matches will all come back unknown.");
        }

        return problems;
    }

    public static bool HasErrors(IEnumerable<ProfileProblem> problems) =>
        problems.Any(p => p.Severity == ProfileProblemSeverity.Error);

    private static IEnumerable<string> ValidateSpecific(RoiDefinition roi, string where)
    {
        switch (roi)
        {
            case SegmentedBarRoi bar:
                if (bar.SegmentCount < 1)
                {
                    yield return $"{where}: segmentCount must be at least 1.";
                }

                if (bar.SegmentGap is < 0 or >= 1)
                {
                    yield return $"{where}: segmentGap must be in [0, 1).";
                }

                if (bar.FilledCoverage is <= 0 or > 1)
                {
                    yield return $"{where}: filledCoverage must be in (0, 1].";
                }

                if (!bar.Filled.IsValid)
                {
                    yield return $"{where}: filled.tolerance must be in (0, 1].";
                }

                break;

            case FillBarRoi fill:
                if (fill.ReadyRatio is <= 0 or > 1)
                {
                    yield return $"{where}: readyRatio must be in (0, 1].";
                }

                if (!fill.Fill.IsValid)
                {
                    yield return $"{where}: fill.tolerance must be in (0, 1].";
                }

                break;

            case DiscStateRoi disc:
                if (disc.InnerRadius is <= 0 or > 1)
                {
                    yield return $"{where}: innerRadius must be in (0, 1].";
                }

                if (disc.ReadyLuma is < 0 or > 255)
                {
                    yield return $"{where}: readyLuma must be in [0, 255].";
                }

                if (disc.ReadySaturation is < 0 or > 1)
                {
                    yield return $"{where}: readySaturation must be in [0, 1].";
                }

                break;

            case PortraitSlotRoi slot:
                if (slot.InnerRadius is <= 0 or > 1)
                {
                    yield return $"{where}: innerRadius must be in (0, 1].";
                }

                if (slot.MaxHashDistance is < 0 or > 64)
                {
                    yield return $"{where}: maxHashDistance must be in [0, 64] for a 64-bit hash.";
                }

                break;

            case PresenceRoi presence:
                if (presence.MinimumCoverage is <= 0 or > 1)
                {
                    yield return $"{where}: minimumCoverage must be in (0, 1].";
                }

                if (!presence.Signature.IsValid)
                {
                    yield return $"{where}: signature.tolerance must be in (0, 1].";
                }

                break;
        }
    }
}
