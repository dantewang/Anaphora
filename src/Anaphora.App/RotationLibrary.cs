using System.Text.Json;
using Anaphora.Core;

namespace Anaphora.App;

/// <summary>A rotation file in the library, or the reason it could not be read.</summary>
internal sealed record LibraryEntry(string Path, Rotation? Rotation, string? Error)
{
    public string Id => Rotation?.Id ?? System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The user's rotations: one JSON file per rotation in
/// <c>%LOCALAPPDATA%\Anaphora\rotations</c>, named after its id. The rotations
/// shipped with the app are copied in the first time they are missing, so
/// editing one never touches the copy next to the executable.
/// </summary>
internal sealed class RotationLibrary
{
    public RotationLibrary(string? directory = null)
    {
        Directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anaphora", "rotations");
    }

    public string Directory { get; }

    /// <summary>Copies in any shipped rotation the library does not have yet.</summary>
    public void Seed(string shippedDirectory)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!System.IO.Directory.Exists(shippedDirectory))
        {
            return;
        }

        foreach (string shipped in System.IO.Directory.EnumerateFiles(shippedDirectory, "*.json"))
        {
            string target = System.IO.Path.Combine(Directory, System.IO.Path.GetFileName(shipped));
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                // Written back in the current schema, so the library never holds an old one.
                RotationStore.Save(target, RotationStore.Load(shipped));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Write($"shipped rotation {shipped} not copied: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<LibraryEntry> Load()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var entries = new List<LibraryEntry>();
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                entries.Add(new LibraryEntry(path, RotationStore.Load(path), null));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                entries.Add(new LibraryEntry(path, null, ex.Message));
            }
        }

        return entries;
    }

    /// <returns>Where it was written.</returns>
    public string Save(Rotation rotation)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathFor(rotation.Id);
        RotationStore.Save(path, rotation);
        return path;
    }

    /// <summary>An id not used by any file in the library, based on <paramref name="wanted"/>.</summary>
    public string FreeId(string wanted)
    {
        string stem = Slug(wanted);
        string id = stem;
        for (int n = 2; File.Exists(PathFor(id)); n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }

    private string PathFor(string id) => System.IO.Path.Combine(Directory, $"{Slug(id)}.json");

    /// <summary>Ids become file names: keep them to characters every file system takes.</summary>
    private static string Slug(string id)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string slug = new([.. id.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c)]);
        return slug.Length == 0 ? "rotation" : slug;
    }
}
