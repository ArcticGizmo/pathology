using System.Text.Json;
using System.Text.Json.Serialization;
using Pathology.Core.Store;

namespace Pathology.Core.Model;

/// <summary>
/// The snapshot's JSON form: camelCase, enums and flags as names, indented. Fixtures and exported snapshots
/// use it; M8's baseline/drift diffs two of them.
/// </summary>
public static class PathSnapshotJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(PathSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static PathSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<PathSnapshot>(json, Options) ?? throw new JsonException("The snapshot JSON was null.");

    /// <summary>Read a snapshot file, or null when missing or unreadable.</summary>
    public static PathSnapshot? Read(string path) => JsonFile.Read<PathSnapshot>(path, Options);

    /// <summary>
    /// Write a snapshot atomically. Refuses an unredacted one: nothing captured from a real machine should reach
    /// disk with its usernames, SIDs and machine name intact (see CLAUDE.md).
    /// </summary>
    public static void Write(string path, PathSnapshot snapshot)
    {
        if (!snapshot.Redacted)
            throw new InvalidOperationException("Refusing to write an unredacted snapshot. Run it through SnapshotRedactor first.");
        JsonFile.Write(path, snapshot, Options);
    }
}
