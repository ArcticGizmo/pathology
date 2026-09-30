using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Redaction;

namespace Pathology.App.Cli;

/// <summary>
/// <c>pathology snapshot [file]</c>: scan this machine and write a <b>redacted</b> snapshot, the raw material for
/// test fixtures and bug reports. The scan is read-only and never probes the network. The only thing written
/// is the output file, and only after the leak check passes. The console shows counts, never paths or names.
/// </summary>
internal static class SnapshotCommand
{
    public const string DefaultOutput = "captures/snapshot-redacted.json";

    public static int Run(string? output)
    {
        output ??= DefaultOutput;
        using var services = new AppServices(Path.Combine(Path.GetTempPath(), "pathology-snapshot-" + Guid.NewGuid().ToString("N")));

        var snapshot = services.Capture(new CaptureOptions { ProbeNetworkPaths = false });
        var redacted = SnapshotRedactor.Redact(snapshot);

        var leaks = SnapshotRedactor.FindLeaks(PathSnapshotJson.Serialize(redacted), snapshot.Host);
        if (leaks.Count > 0)
        {
            Console.Error.WriteLine($"not written: redaction left identifying values behind ({string.Join(", ", leaks)})");
            return 2;
        }

        PathSnapshotJson.Write(output, redacted);

        var dirs = redacted.Directories;
        Console.WriteLine(
            $"entries: {redacted.EntriesIn(PathScope.Machine).Count()} machine, {redacted.EntriesIn(PathScope.User).Count()} user; " +
            $"directories: {dirs.Count} ({dirs.Count(d => d.Exists)} exist, {dirs.Count(d => d.Status == ProbeStatus.SkippedNetwork)} network skipped, " +
            $"{dirs.Count(d => d.Status == ProbeStatus.Failed)} failed, {dirs.Count(d => d.SecurityError is not null)} unreadable ACLs); " +
            $"access checks: {dirs.Sum(d => d.Access.Count(a => a.Evaluated))} ok, {dirs.Sum(d => d.Access.Count(a => !a.Evaluated))} failed");
        Console.WriteLine($"redacted snapshot written to {Path.GetFullPath(output)}");
        return 0;
    }
}
