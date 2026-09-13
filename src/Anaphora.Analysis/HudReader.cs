using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>
/// Everything one frame had to say. When <see cref="HudPresent"/> is false the
/// frame carried no HUD at all -- out of combat, mid ultimate cut-in, behind a
/// chain cut-in -- and every other field is meaningless. The overlay should hold
/// the previous values and mark them stale rather than show these.
/// </summary>
public sealed record HudSnapshot
{
    public static readonly HudSnapshot Absent = new();

    public bool HudPresent { get; init; }

    public PresenceReading Presence { get; init; }

    /// <summary>Whole skill points banked, shared across the team.</summary>
    public int SkillPoints { get; init; }

    /// <summary>Chain cooldowns in screen order, which is character order.</summary>
    public IReadOnlyList<FillBarReading> Chains { get; init; } = [];

    /// <summary>Ultimate slots in screen order, matching the 1-4 keys.</summary>
    public IReadOnlyList<DiscStateReading> Ultimates { get; init; } = [];

    /// <summary>Chain prompts, highest priority first: index 0 is what the key triggers.</summary>
    public IReadOnlyList<PortraitReading> Prompts { get; init; } = [];
}

/// <summary>
/// Runs a whole profile against a frame.
///
/// The sentinel is checked first and short-circuits everything else. Reading a
/// bar out of a frame whose HUD is not there does not produce a slightly wrong
/// number, it produces a confident one drawn from scenery.
/// </summary>
public sealed class HudReader
{
    private readonly PresenceRoi? sentinel;
    private readonly SegmentedBarRoi? skillPoints;
    private readonly FillBarRoi[] chains;
    private readonly DiscStateRoi[] ultimates;
    private readonly PortraitSlotRoi[] prompts;
    private readonly PortraitReference[] portraits;

    public HudReader(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
        sentinel = profile.HudSentinelRoiId is { } id ? profile.FindRoi(id) as PresenceRoi : null;
        skillPoints = profile.RoisOf<SegmentedBarRoi>().FirstOrDefault();

        // Screen order is character order: the team panel, the chain bars under
        // it and the 1-4 ultimate keys all run left to right in the same order.
        chains = [.. profile.RoisOf<FillBarRoi>().OrderBy(r => r.Bounds.X)];
        ultimates = [.. profile.RoisOf<DiscStateRoi>().OrderBy(r => r.Bounds.X)];
        prompts = [.. profile.RoisOf<PortraitSlotRoi>().OrderBy(r => r.Priority)];
        portraits = [.. profile.Portraits];
    }

    public GameProfile Profile { get; }

    /// <summary>True when the profile has no sentinel, in which case every frame is read.</summary>
    public bool ReadsBlind => sentinel is null;

    /// <summary>Reads a frame whose coordinates are already the client area.</summary>
    public HudSnapshot Read(in FrameView frame) => ReadCore(frame, null);

    /// <summary>
    /// Reads the atlas the capture pipeline produced. The atlas must have been
    /// laid out from this reader's profile: slots are looked up by ROI id, and a
    /// different profile would hand the readers another ROI's pixels.
    /// </summary>
    public HudSnapshot Read(in FrameView atlasPixels, RoiAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (!ReferenceEquals(atlas.Profile, Profile) && !atlas.Profile.Equals(Profile))
        {
            throw new ArgumentException("the atlas was laid out for a different profile.", nameof(atlas));
        }

        return ReadCore(atlasPixels, atlas);
    }

    private HudSnapshot ReadCore(in FrameView frame, RoiAtlas? atlas)
    {
        PresenceReading presence;
        if (sentinel is null)
        {
            presence = new PresenceReading(true, 0, 0);
        }
        else if (!View(frame, atlas, sentinel, out FrameView sentinelView))
        {
            presence = new PresenceReading(false, 0, 0);
        }
        else
        {
            presence = RoiReader.Read(sentinel, sentinelView);
        }

        if (!presence.IsPresent)
        {
            return HudSnapshot.Absent with { Presence = presence };
        }

        var chainReadings = new FillBarReading[chains.Length];
        for (int i = 0; i < chains.Length; i++)
        {
            chainReadings[i] = View(frame, atlas, chains[i], out FrameView view)
                ? RoiReader.Read(chains[i], view)
                : default;
        }

        var ultimateReadings = new DiscStateReading[ultimates.Length];
        for (int i = 0; i < ultimates.Length; i++)
        {
            ultimateReadings[i] = View(frame, atlas, ultimates[i], out FrameView view)
                ? RoiReader.Read(ultimates[i], view)
                : default;
        }

        var promptReadings = new PortraitReading[prompts.Length];
        for (int i = 0; i < prompts.Length; i++)
        {
            promptReadings[i] = View(frame, atlas, prompts[i], out FrameView view)
                ? RoiReader.Read(prompts[i], view, portraits)
                : new PortraitReading(null, 64, 0);
        }

        int banked = skillPoints is not null && View(frame, atlas, skillPoints, out FrameView skillView)
            ? RoiReader.Read(skillPoints, skillView).Filled
            : 0;

        return new HudSnapshot
        {
            HudPresent = true,
            Presence = presence,
            SkillPoints = banked,
            Chains = chainReadings,
            Ultimates = ultimateReadings,
            Prompts = promptReadings,
        };
    }

    private static bool View(in FrameView frame, RoiAtlas? atlas, RoiDefinition roi, out FrameView view)
    {
        if (atlas is null)
        {
            view = frame;
            return true;
        }

        return atlas.TryViewFor(frame, roi, out view);
    }
}
