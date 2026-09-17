using System.Globalization;

namespace Anaphora.App;

/// <summary>
/// Command-line switches. Everything here is for development and diagnosis;
/// with no arguments the app finds the game from the profile and runs.
///
///   --profile PATH       a different profile (default profiles/endfield.json)
///   --rotation PATH      follow this rotation file instead of the library's active one
///   --process NAME       capture this process instead of the profile's -- any window
///                        will do for checking the overlay mechanics without the game
///   --any-foreground     show the overlay even when the captured window is not in front
///   --rate HZ            readings per second (default 20)
/// </summary>
internal sealed record AppOptions
{
    public static AppOptions Current { get; set; } = new();

    public string ProfilePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json");

    /// <summary>Follow this file instead of the library's active rotation.</summary>
    public string? RotationPath { get; init; }

    public string? ProcessName { get; init; }

    public bool RequireForeground { get; init; } = true;

    public double Rate { get; init; } = 20;

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            options = args[i] switch
            {
                "--profile" => options with { ProfilePath = Path.GetFullPath(Next() ?? options.ProfilePath) },
                "--rotation" => options with { RotationPath = Next() is { } rotation ? Path.GetFullPath(rotation) : null },
                "--process" => options with { ProcessName = Next() },
                "--any-foreground" => options with { RequireForeground = false },
                "--rate" => options with
                {
                    Rate = double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) ? rate : options.Rate,
                },
                _ => options,
            };
        }

        return options;
    }
}
