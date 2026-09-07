using Anaphora.Core;

namespace Anaphora.Core.Tests;

internal static class Sample
{
    public static GameProfile Profile(params RoiDefinition[] rois) => new()
    {
        Id = "sample",
        DisplayName = "Sample",
        Window = new WindowMatch { ProcessName = "Sample", WindowClass = "UnityWndClass" },
        HudSentinelRoiId = "hud",
        Rois = [Sentinel, .. rois],
    };

    public static PresenceRoi Sentinel => new()
    {
        Id = "hud",
        Bounds = new NormalizedRect(0.4, 0.89, 0.18, 0.02),
    };
}

public class ProfileStoreTests
{
    [Fact]
    public void RoundTripPreservesEveryRoiKind()
    {
        GameProfile original = Sample.Profile(
            new SegmentedBarRoi
            {
                Id = "skillPoints",
                Label = "技能点",
                Bounds = new NormalizedRect(0.414063, 0.900463, 0.171875, 0.011574),
                SegmentCount = 3,
                Filled = new ColourGate(new Rgb(0xE5, 0xD1, 0x4B), 0.18),
            },
            new FillBarRoi
            {
                Id = "chain.0",
                Bounds = new NormalizedRect(0.02, 0.912, 0.05, 0.005),
                Direction = FillDirection.LeftToRight,
                Fill = new ColourGate(new Rgb(0xF0, 0xF0, 0xF0), 0.13),
            },
            new DiscStateRoi
            {
                Id = "ultimate.0",
                Bounds = new NormalizedRect(0.81, 0.808, 0.018, 0.031),
            },
            new PortraitSlotRoi
            {
                Id = "chainPrompt.0",
                Bounds = new NormalizedRect(0.594, 0.422, 0.039, 0.069),
                Priority = 0,
            });

        GameProfile round = ProfileStore.Deserialize(ProfileStore.Serialize(original));

        Assert.Equal(original, round);
    }

    [Fact]
    public void EqualityLooksInsideTheRoiList()
    {
        // The compiler-generated record equality would compare the lists by
        // reference and call these equal regardless of contents.
        GameProfile one = Sample.Profile(
            new DiscStateRoi { Id = "ult", Bounds = new NormalizedRect(0.81, 0.808, 0.018, 0.031) });
        GameProfile same = Sample.Profile(
            new DiscStateRoi { Id = "ult", Bounds = new NormalizedRect(0.81, 0.808, 0.018, 0.031) });
        GameProfile moved = Sample.Profile(
            new DiscStateRoi { Id = "ult", Bounds = new NormalizedRect(0.86, 0.808, 0.018, 0.031) });

        Assert.Equal(one, same);
        Assert.Equal(one.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(one, moved);
    }

    [Fact]
    public void ColoursAreWrittenAsHexStrings()
    {
        string json = ProfileStore.Serialize(Sample.Profile(new SegmentedBarRoi
        {
            Id = "skillPoints",
            Bounds = new NormalizedRect(0.414063, 0.900463, 0.171875, 0.011574),
            SegmentCount = 3,
            Filled = new ColourGate(new Rgb(0xF8, 0xF8, 0x00), 0.20),
        }));

        Assert.Contains("\"#F8F800\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumsAreWrittenAsCamelCaseNames()
    {
        string json = ProfileStore.Serialize(Sample.Profile(new FillBarRoi
        {
            Id = "chain.0",
            Bounds = new NormalizedRect(0.02, 0.912, 0.05, 0.005),
            Direction = FillDirection.BottomToTop,
            Fill = new ColourGate(new Rgb(0xF0, 0xF0, 0xF0), 0.13),
        }));

        Assert.Contains("\"bottomToTop\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void KindDiscriminatorNamesTheRoiType()
    {
        string json = ProfileStore.Serialize(Sample.Profile());

        Assert.Contains("\"kind\": \"presence\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        // Profiles get hand-edited while calibrating; a stray comma should not
        // take the whole thing down.
        const string Json = """
            {
              // provisional
              "schemaVersion": 1,
              "id": "sample",
              "displayName": "Sample",
              "window": { "processName": "Sample" },
              "rois": [],
            }
            """;

        GameProfile profile = ProfileStore.Deserialize(Json);

        Assert.Equal("sample", profile.Id);
    }

    [Fact]
    public void PortraitSlotFindsItselfById()
    {
        GameProfile profile = Sample.Profile(new PortraitSlotRoi
        {
            Id = "chainPrompt.0",
            Bounds = new NormalizedRect(0.594, 0.422, 0.039, 0.069),
        });

        Assert.IsType<PortraitSlotRoi>(profile.FindRoi("chainPrompt.0"));
        Assert.Null(profile.FindRoi("nope"));
    }
}

public class ProfileValidatorTests
{
    private static string[] Errors(GameProfile profile) =>
        [.. ProfileValidator.Validate(profile)
            .Where(p => p.Severity == ProfileProblemSeverity.Error)
            .Select(p => p.Message)];

    [Fact]
    public void AWellFormedProfileHasNoErrors()
    {
        Assert.Empty(Errors(Sample.Profile()));
    }

    [Fact]
    public void DuplicateIdsAreAnError()
    {
        GameProfile profile = Sample.Profile(
            new DiscStateRoi { Id = "dup", Bounds = new NormalizedRect(0.1, 0.1, 0.1, 0.1) },
            new DiscStateRoi { Id = "dup", Bounds = new NormalizedRect(0.3, 0.1, 0.1, 0.1) });

        Assert.Contains(Errors(profile), m => m.Contains("duplicate id", StringComparison.Ordinal));
    }

    [Fact]
    public void BoundsOffTheEdgeAreAnError()
    {
        GameProfile profile = Sample.Profile(
            new DiscStateRoi { Id = "off", Bounds = new NormalizedRect(0.95, 0.1, 0.2, 0.1) });

        Assert.Contains(Errors(profile), m => m.Contains("outside the frame", StringComparison.Ordinal));
    }

    [Fact]
    public void ADanglingSentinelIsAnError()
    {
        GameProfile profile = Sample.Profile() with { HudSentinelRoiId = "missing" };

        Assert.Contains(Errors(profile), m => m.Contains("does not match any roi", StringComparison.Ordinal));
    }

    [Fact]
    public void ASentinelPointingAtTheWrongKindIsAnError()
    {
        GameProfile profile = Sample.Profile(
            new DiscStateRoi { Id = "ult", Bounds = new NormalizedRect(0.8, 0.8, 0.02, 0.03) })
            with
        { HudSentinelRoiId = "ult" };

        Assert.Contains(Errors(profile), m => m.Contains("not a PresenceRoi", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingSentinelIsOnlyAWarning()
    {
        GameProfile profile = Sample.Profile() with { HudSentinelRoiId = null };
        IReadOnlyList<ProfileProblem> problems = ProfileValidator.Validate(profile);

        Assert.False(ProfileValidator.HasErrors(problems));
        Assert.Contains(problems, p => p.Message.Contains("hudSentinelRoiId", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedPortraitPrioritiesAreAnError()
    {
        GameProfile profile = Sample.Profile(
            new PortraitSlotRoi { Id = "a", Bounds = new NormalizedRect(0.5, 0.4, 0.04, 0.07), Priority = 0 },
            new PortraitSlotRoi { Id = "b", Bounds = new NormalizedRect(0.6, 0.4, 0.04, 0.07), Priority = 0 });

        Assert.Contains(Errors(profile), m => m.Contains("priority 0", StringComparison.Ordinal));
    }

    [Fact]
    public void PortraitSlotsWithNoReferencesAreOnlyAWarning()
    {
        GameProfile profile = Sample.Profile(
            new PortraitSlotRoi { Id = "a", Bounds = new NormalizedRect(0.5, 0.4, 0.04, 0.07) });

        Assert.False(ProfileValidator.HasErrors(ProfileValidator.Validate(profile)));
    }

    [Fact]
    public void OutOfRangeThresholdsAreAnError()
    {
        GameProfile profile = Sample.Profile(new SegmentedBarRoi
        {
            Id = "sp",
            Bounds = new NormalizedRect(0.4, 0.9, 0.17, 0.01),
            SegmentCount = 0,
            FilledCoverage = 2,
            Filled = new ColourGate(new Rgb(1, 2, 3), 0),
        });

        string[] errors = Errors(profile);

        Assert.Contains(errors, m => m.Contains("segmentCount", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("filledCoverage", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("tolerance", StringComparison.Ordinal));
    }
}
