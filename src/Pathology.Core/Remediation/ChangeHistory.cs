using System.Text.Json.Serialization;
using Pathology.Core.Model;
using Pathology.Core.Store;

namespace Pathology.Core.Remediation;

/// <summary>How an apply ended.</summary>
public enum ChangeOutcome
{
    /// <summary>The backup is written and the writes haven't finished (or the app stopped part-way).</summary>
    Pending,
    Applied,

    /// <summary>Some writes were applied and some weren't.</summary>
    Partial,
    Failed,

    /// <summary>The UAC prompt was declined: nothing was written.</summary>
    Cancelled,

    /// <summary>Refused before anything was written (it changed since the scan, or it would break a limit).</summary>
    Refused,
}

/// <summary>
/// One apply: the change set (which is also the backup: every "before" is in it), what happened to each write, and
/// whether it's been undone. Written before the first write, and again when it finishes.
/// </summary>
public sealed record ChangeRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>One line: "3 fixes", "Undo of 3 fixes".</summary>
    public string Summary { get; init; } = "";

    /// <summary>The titles of the fixes it applied, for the History page.</summary>
    public IReadOnlyList<string> Fixes { get; init; } = [];

    public required ChangeSet Changes { get; init; }
    public ChangeOutcome Outcome { get; init; }
    public IReadOnlyList<StepResult> Steps { get; init; } = [];

    /// <summary>Why it was refused or failed as a whole, or null.</summary>
    public string? Message { get; init; }

    /// <summary>The record this one undid, or null.</summary>
    public Guid? UndoOf { get; init; }

    /// <summary>The record that undid this one, or null.</summary>
    public Guid? UndoneBy { get; init; }

    [JsonIgnore] public IEnumerable<StepResult> Applied => Steps.Where(s => s.Status == StepStatus.Applied);

    /// <summary>Something was written and it hasn't been undone yet.</summary>
    [JsonIgnore] public bool CanUndo => UndoneBy is null && Applied.Any();

    public bool WasApplied(string target) =>
        Steps.Any(s => s.Status == StepStatus.Applied && string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A change set that puts back what this one changed: before is the live state now, after is this record's
    /// before. Only writes that were applied are reversed.
    /// </summary>
    public ChangeSet Undo(Func<PathScope, RawPathValue> currentValue, Func<string, string?> currentSddl)
    {
        var values = Changes.Values
            .Where(v => WasApplied(StepResult.TargetOf(v.Scope)))
            .Select(v => new ValueChange { Before = currentValue(v.Scope), After = v.Before })
            .ToList();
        var acls = Changes.Acls
            .Where(a => a.Writes && WasApplied(a.Path))
            .Select(a => currentSddl(a.Path) is { } now
                ? a with
                {
                    BeforeSddl = now,
                    AfterSddl = a.BeforeSddl,
                    AfterOwnerSid = null,
                    Lines = ["Back to the permissions it had before"],
                    Commands = [],
                    CoveredBy = null,
                }
                : a with { Refusal = "Its permissions can't be read now, so they can't be put back." })
            .ToList();
        return new ChangeSet { Values = values, Acls = acls };
    }
}

/// <summary>Every apply, newest first. The local backup store: never exported.</summary>
public interface IChangeHistory
{
    void Save(ChangeRecord record);
    IReadOnlyList<ChangeRecord> All();
    ChangeRecord? Get(Guid id);
}

/// <summary>One JSON file per record under <see cref="IPathologyPaths.HistoryDirectory"/>.</summary>
public sealed class FileChangeHistory(IPathologyPaths paths) : IChangeHistory
{
    string FileOf(Guid id) => Path.Combine(paths.HistoryDirectory, $"{id:N}.json");

    public void Save(ChangeRecord record) => JsonFile.Write(FileOf(record.Id), record, PathSnapshotJson.Options);

    public ChangeRecord? Get(Guid id) => JsonFile.Read<ChangeRecord>(FileOf(id), PathSnapshotJson.Options);

    public IReadOnlyList<ChangeRecord> All()
    {
        if (!Directory.Exists(paths.HistoryDirectory)) return [];
        return Directory.EnumerateFiles(paths.HistoryDirectory, "*.json")
            .Select(f => JsonFile.Read<ChangeRecord>(f, PathSnapshotJson.Options))
            .OfType<ChangeRecord>()
            .OrderByDescending(r => r.StartedAt)
            .ToList();
    }
}
