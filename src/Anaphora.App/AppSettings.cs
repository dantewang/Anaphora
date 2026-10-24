using System.Text.Json;
using Anaphora.Overlay;

namespace Anaphora.App;

/// <summary>
/// Where the overlay sits, as fractions of the game's client area: x is its
/// horizontal centre, y its bottom edge. Normalised for the same reason ROI
/// coordinates are -- changing resolution or going windowed must not move it.
/// </summary>
internal sealed record AnchorSetting(double X, double Y)
{
    public static AnchorSetting From(OverlayAnchor anchor) => new(anchor.X, anchor.Y);

    public OverlayAnchor ToAnchor() => new(X, Y);
}

/// <summary>What the user changed and expects to find again next time.</summary>
internal sealed record AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anaphora", "settings.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Whether the overlay draws the rotation strip under the title card. On by
    /// default; off leaves only "what to press now".
    /// </summary>
    public bool TrackVisible { get; init; } = true;

    /// <summary>
    /// Hide the overlay once the HUD has been unreadable for <see cref="HideAfterSeconds"/>:
    /// menus, the map, walking between fights. Will be a switch in the main window.
    /// </summary>
    public bool HideWhenHudAbsent { get; init; } = true;

    /// <summary>Long enough to ride out an ultimate's cut-in without flickering.</summary>
    public double HideAfterSeconds { get; init; } = 5;

    /// <summary>Sends the rotation back to its start. Empty turns the hotkey off.</summary>
    public string? ResetHotkey { get; init; } = "Ctrl+Alt+R";

    /// <summary>
    /// Turns dragging the overlay on and off. Empty turns the hotkey off, and
    /// the tray still has the switch.
    /// </summary>
    public string? MoveHotkey { get; init; } = "Ctrl+Alt+M";

    /// <summary>Where the user dragged the overlay to; null means the default place.</summary>
    public AnchorSetting? OverlayAnchor { get; init; }

    /// <summary>The id of the library rotation the overlay follows.</summary>
    public string ActiveRotation { get; init; } = "endfield-example";

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"settings unreadable, using defaults: {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings not saved: {ex.Message}");
        }
    }
}

/// <summary>
/// A plain append-only log next to the settings. The app has no console, and on
/// the gaming machine this file is the only way to see why the overlay is not up.
/// </summary>
internal static class Log
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anaphora", "app.log");

    private static readonly Lock Gate = new();

    public static string Location => FilePath;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
