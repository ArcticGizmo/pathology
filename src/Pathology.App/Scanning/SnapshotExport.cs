using Pathology.Core.Model;
using Pathology.Core.Redaction;

namespace Pathology.App.Scanning;

/// <summary>
/// Writes a snapshot for a bug report: redacted first, then checked for anything identifying the redactor missed.
/// Shared by Settings' export button and <c>pathology snapshot</c>, so both refuse the same way.
/// </summary>
internal static class SnapshotExport
{
    /// <summary>Redact and write. Returns null on success, or why nothing was written (categories only, never values).</summary>
    public static string? Write(PathSnapshot snapshot, string path)
    {
        var redacted = snapshot.Redacted ? snapshot : SnapshotRedactor.Redact(snapshot);
        var leaks = SnapshotRedactor.FindLeaks(PathSnapshotJson.Serialize(redacted), snapshot.Host);
        if (leaks.Count > 0) return $"Not written: redaction left identifying values behind ({string.Join(", ", leaks)}).";
        PathSnapshotJson.Write(path, redacted);
        return null;
    }
}
