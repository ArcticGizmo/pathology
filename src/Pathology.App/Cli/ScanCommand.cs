using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Redaction;
using Pathology.Core.Scoring;

namespace Pathology.App.Cli;

/// <summary>
/// <c>pathology scan [--redact] [--details] [--all] [--from snapshot.json]</c>: diagnose this machine (or a saved snapshot)
/// and print the findings as plain text, grouped by root cause. Read-only and never probes the network. A dev
/// and verification aid, not the M7 reporting contract. The exit code is the number of Critical and High groups.
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
            return diagnosis.Groups.Count(g => g.Severity >= Severity.High);
        }

        Console.WriteLine($"PATHology scan: {snapshot.EntriesIn(PathScope.Machine).Count()} machine and " +
                          $"{snapshot.EntriesIn(PathScope.User).Count()} user entries, {diagnosis.Shadows.All.Count} commands" +
                          (snapshot.Redacted ? " (redacted)" : ""));

        var score = HealthScorer.Score(diagnosis);
        var band = score.Band switch
        {
            HealthBand.Healthy => "healthy",
            HealthBand.Fair => "fair",
            HealthBand.NeedsAttention => "needs attention",
            _ => "at risk",
        };
        Console.WriteLine($"Health: {score.Overall}% ({band}). Security {score.Categories[FindingCategory.Security]}, " +
                          $"correctness {score.Categories[FindingCategory.Correctness]}, hygiene {score.Categories[FindingCategory.Hygiene]}" +
                          (score.CappedBy is { } cap ? $"; held at {score.Overall} by a {cap.ToString().ToLowerInvariant()} problem (would be {score.Uncapped})" : ""));
        if (score.Hint is { } hint)
            Console.WriteLine($"Fix \"{hint.Problem.Primary.Title}\" to reach {hint.Reaches}%.");
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

        var counts = Enum.GetValues<Severity>().Reverse().Select(s => $"{diagnosis.Groups.Count(g => g.Severity == s)} {s.ToString().ToLowerInvariant()}");
        Console.WriteLine();
        Console.WriteLine($"{diagnosis.Groups.Count} problems ({diagnosis.Findings.Count} findings): {string.Join(", ", counts)}");
        return diagnosis.Groups.Count(g => g.Severity >= Severity.High);
    }
}
