using System.Text.Json;

namespace Anaphora.App;

/// <summary>What the user changed and expects to find again next time.</summary>
internal sealed record AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anaphora", "settings.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Open by default: early on, the readings need checking against the game.</summary>
    public bool StatusVisible { get; init; } = true;

    /// <summary>
    /// Hide the overlay once the HUD has been unreadable for <see cref="HideAfterSeconds"/>:
    /// menus, the map, walking between fights. Will be a switch in the main window.
    /// </summary>
    public bool HideWhenHudAbsent { get; init; } = true;

    /// <summary>Long enough to ride out an ultimate's cut-in without flickering.</summary>
    public double HideAfterSeconds { get; init; } = 5;

    /// <summary>Sends the rotation back to its start. Empty turns the hotkey off.</summary>
    public string? ResetHotkey { get; init; } = "Ctrl+Alt+R";

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
