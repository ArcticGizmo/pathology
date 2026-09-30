using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Redaction;
using Pathology.Core.Health;

namespace Pathology.App.Cli;

/// <summary>
/// <c>pathology scan [--redact] [--details] [--all] [--from snapshot.json]</c>: diagnose this machine (or a saved snapshot)
/// and print the findings as plain text, grouped by root cause. Read-only and never probes the network. A dev
/// and verification aid, not the M7 reporting contract. The exit code is the number of High problems.
/// </summary>
/// <remarks>
/// <c>--redact</c> scrubs the snapshot before diagnosing it, so the output carries placeholders instead of this
/// machine's usernames, SIDs and names: safe to paste into a bug report.
/// </remarks>
internal static class ScanCommand
{
    public static int Run(string[] args)
    {
        var redact = args.Contains("--redact");
        var details = args.Contains("--details");
        var fromIndex = Array.IndexOf(args, "--from");

        PathSnapshot snapshot;
        if (fromIndex >= 0 && fromIndex + 1 < args.Length)
        {
            if (PathSnapshotJson.Read(args[fromIndex + 1]) is not { } saved)
            {
                Console.Error.WriteLine("couldn't read that snapshot");
                return -1;
            }
            snapshot = saved;
        }
        else
        {
            using var services = new AppServices(Path.Combine(Path.GetTempPath(), "pathology-scan-" + Guid.NewGuid().ToString("N")));
            snapshot = services.Capture(new CaptureOptions { ProbeNetworkPaths = false });
        }
        if (redact && !snapshot.Redacted) snapshot = SnapshotRedactor.Redact(snapshot);

        var diagnosis = Diagnoser.Diagnose(snapshot);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--all"))
        {
            foreach (var f in diagnosis.Findings)
                Console.WriteLine($"{f.Severity,-8}  {f.Rule}  {f.RootCause}  {f.Title}");
            return HealthRater.Rate(diagnosis).Count(Severity.High);
        }

        Console.WriteLine($"PATHology scan: {snapshot.EntriesIn(PathScope.Machine).Count()} machine and " +
                          $"{snapshot.EntriesIn(PathScope.User).Count()} user entries, {diagnosis.Shadows.All.Count} commands" +
                          (snapshot.Redacted ? " (redacted)" : ""));

        Console.WriteLine();
        var health = HealthRater.Rate(diagnosis);
        foreach (var category in health.Categories)
        {
            var counts = new[] { Severity.High, Severity.Medium, Severity.Low }
                .Where(s => category.Count(s) > 0)
                .Select(s => $"{category.Count(s)} {Word(s)}");
            var next = category.IsClean ? ""
                : $"  (fix {category.Holding.Count()} to reach {(category.AfterHolding is { } after ? Word(after) : "clean")})";
            Console.WriteLine($"{category.Category.ToString().ToUpperInvariant(),-12} {(category.Rating is { } r ? Word(r).ToUpperInvariant() : "CLEAN"),-7} " +
                              string.Join(" · ", counts) + next);
        }
        if (health.Notes.Count > 0) Console.WriteLine($"{"NOTES",-12} {health.Notes.Count}");
        Console.WriteLine();

        foreach (var group in diagnosis.Groups)
        {
            var f = group.Primary;
            var also = group.Related.Select(r => r.Rule).Distinct().ToList();
            Console.WriteLine($"{f.Severity.ToString().ToUpperInvariant(),-8}  {f.Rule}  {f.Title}" + (also.Count > 0 ? $"  (also {string.Join(", ", also)})" : ""));
            if (details)
            {
                Console.WriteLine($"          What: {f.What}");
                Console.WriteLine($"          Why:  {f.Why}");
                Console.WriteLine($"          Fix:  {f.Fix}");
            }
            foreach (var line in f.Evidence) Console.WriteLine($"          - {line}");
        }

        return health.Count(Severity.High);
    }

    static string Word(Severity severity) => severity == Severity.Info ? "note" : severity.ToString().ToLowerInvariant();
}
