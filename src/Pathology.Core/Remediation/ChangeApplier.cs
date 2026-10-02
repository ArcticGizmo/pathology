using System.Security.Cryptography;
using System.Text.Json;
using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Core.Remediation;

/// <summary>
/// Applies a change set, safely: refuse if anything changed since the scan, write the backup record first, run the
/// part that needs an administrator first (so a declined UAC prompt changes nothing), then the part you can do as
/// yourself, read each write back, tell Explorer, and record what happened step by step.
/// </summary>
/// <remarks>
/// The exception is an entry moving from the machine PATH to the user PATH (<see cref="ChangeSet.UserFirst"/>): the
/// user PATH gets its copy before the UAC prompt, and is put back if the prompt is declined.
/// <para>The writers are injected. Only <c>AppServices</c> hands this the real ones, and only from a click on Apply.</para>
/// </remarks>
public sealed class ChangeApplier(
    IPathValueStore values, IAclStore acls, IEnvironmentBroadcast broadcast, IElevatedRunner elevated,
    IChangeHistory history, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <param name="changes">What to write.</param>
    /// <param name="summary">One line for History.</param>
    /// <param name="fixes">The titles of the fixes it applies.</param>
    /// <param name="undoOf">The record this undoes, if it's an undo.</param>
    public ChangeRecord Apply(ChangeSet changes, string summary, IReadOnlyList<string>? fixes = null, Guid? undoOf = null)
    {
        var record = new ChangeRecord
        {
            Id = changes.Id,
            StartedAt = _clock.GetUtcNow(),
            Summary = summary,
            Fixes = fixes ?? [],
            Changes = changes,
            UndoOf = undoOf,
            Outcome = ChangeOutcome.Pending,
        };

        // Refusals happen before anything is written, so they leave no record behind.
        if (changes.IsEmpty) return Finish(record with { Outcome = ChangeOutcome.Refused, Message = "There's nothing to change." }, save: false);
        if (changes.Problems() is { Count: > 0 } problems)
            return Finish(record with { Outcome = ChangeOutcome.Refused, Message = string.Join(" ", problems) }, save: false);
        if (Conflicts(changes) is { Count: > 0 } conflicts)
            return Finish(record with
            {
                Outcome = ChangeOutcome.Refused,
                Message = $"{string.Join(", ", conflicts)} changed after the scan. Nothing was written: re-scan and try again.",
            }, save: false);

        // The backup: every "before" is in the record, on disk, before the first write.
        history.Save(record);

        var steps = new List<StepResult>();
        var user = changes.ValueFor(PathScope.User);

        // An entry moving down from the machine PATH goes into the user PATH before it leaves the machine one.
        var first = changes.UserFirst() is { } stage ? new ValueChange { Before = user!.Before, After = stage } : null;
        if (first is not null)
        {
            var step = Steps.WriteValue(values, first);
            if (step.Status != StepStatus.Applied) return Done(record, [step], changes);
            steps.Add(step with { Message = "Written before the machine PATH, so an entry moving from it is never missing from both." });
        }

        var batch = ElevatedBatch.Of(changes);
        if (!batch.IsEmpty)
        {
            var result = elevated.Run(batch);
            if (result.Cancelled || (result.Error is not null && result.Steps.Count == 0))
            {
                var why = result.Cancelled ? "The UAC prompt was declined" : result.Error!.TrimEnd('.');
                if (first is not null && TakeBack(first) is { } stuck)
                    return Done(record with { Message = $"{why}. The copy put in your user PATH first couldn't be taken out again ({stuck}): undo it from History." }, steps, changes);
                return Finish(record with
                {
                    Outcome = result.Cancelled ? ChangeOutcome.Cancelled : ChangeOutcome.Failed,
                    Message = result.Cancelled ? $"{why}, so nothing was changed." : result.Error,
                    Steps = Skipped(changes),
                });
            }
            steps.AddRange(result.Steps);
            if (result.Steps.Any(s => s.Status != StepStatus.Applied))
                return Done(record with { Message = result.Error }, steps, changes);
        }

        if (first is null && user is not null) steps.Add(Steps.WriteValue(values, user));
        else if (first is not null && !Steps.Same(first.After, user!.After))
            steps.Add(Steps.WriteValue(values, new ValueChange { Before = first.After, After = user.After }));
        foreach (var acl in changes.Acls.Where(a => a.Writes && !a.NeedsAdmin))
        {
            if (steps.Any(s => s.Status == StepStatus.Failed)) { steps.Add(new(acl.Path, StepStatus.Skipped)); continue; }
            steps.Add(Steps.WriteAcl(acls, acl));
        }
        return Done(record, steps, changes);
    }

    /// <summary>
    /// Put the user PATH back as it was after its early write, when the machine part never happened (the entry is
    /// still in the machine PATH, so nothing is lost). Null once it's back, else why it isn't.
    /// </summary>
    string? TakeBack(ValueChange first)
    {
        var back = Steps.WriteValue(values, new ValueChange { Before = first.After, After = first.Before });
        return back.Status == StepStatus.Applied ? null : back.Message ?? "it failed";
    }

    ChangeRecord Done(ChangeRecord record, List<StepResult> steps, ChangeSet changes)
    {
        // Anything the steps didn't reach is listed as skipped, so History shows every planned write.
        foreach (var target in Targets(changes).Where(t => steps.All(s => !string.Equals(s.Target, t, StringComparison.OrdinalIgnoreCase))))
            steps.Add(new(target, StepStatus.Skipped));

        var applied = steps.Count(s => s.Status == StepStatus.Applied);
        if (applied > 0) broadcast.Broadcast();

        var outcome = applied == 0 ? ChangeOutcome.Failed : applied == steps.Count ? ChangeOutcome.Applied : ChangeOutcome.Partial;
        var done = Finish(record with { Outcome = outcome, Steps = steps });

        if (record.UndoOf is { } undone && applied > 0 && history.Get(undone) is { } original)
            history.Save(original with { UndoneBy = record.Id });
        return done;
    }

    ChangeRecord Finish(ChangeRecord record, bool save = true)
    {
        var finished = record with { FinishedAt = _clock.GetUtcNow() };
        if (save) history.Save(finished);
        return finished;
    }

    /// <summary>What's no longer as the scan saw it: "the machine PATH", a folder.</summary>
    List<string> Conflicts(ChangeSet changes)
    {
        var conflicts = new List<string>();
        foreach (var v in changes.Values)
        {
            try { if (!Steps.Same(values.Read(v.Scope), v.Before)) conflicts.Add(v.Scope == PathScope.Machine ? "The machine PATH" : "Your user PATH"); }
            catch (Exception) { conflicts.Add(v.Scope == PathScope.Machine ? "The machine PATH" : "Your user PATH"); }
        }
        foreach (var a in changes.Acls.Where(a => a.Writes))
        {
            try { if (!acls.SameOwnPermissions(a.BeforeSddl, acls.Read(a.Path))) conflicts.Add(a.Path); }
            catch (Exception) { conflicts.Add(a.Path); }
        }
        return conflicts;
    }

    static IEnumerable<string> Targets(ChangeSet changes) =>
        changes.Values.Select(v => StepResult.TargetOf(v.Scope)).Concat(changes.Acls.Where(a => a.Writes).Select(a => a.Path));

    static List<StepResult> Skipped(ChangeSet changes) => Targets(changes).Select(t => new StepResult(t, StepStatus.Skipped)).ToList();
}

/// <summary>
/// The elevated helper's side (<c>pathology apply-elevated &lt;batch&gt; &lt;sha256&gt;</c>): check the batch is the one
/// the app asked for, check it only touches what an elevated batch may, then write it step by step.
/// </summary>
public static class ElevatedApplier
{
    public static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    public static byte[] Serialize(ElevatedBatch batch) => JsonSerializer.SerializeToUtf8Bytes(batch, PathSnapshotJson.Options);

    /// <param name="content">The batch file's bytes, read once: these are what's hashed and what's parsed.</param>
    /// <param name="expectedHash">The SHA-256 the app passed on the command line, in hex.</param>
    public static ElevatedResult Run(byte[] content, string expectedHash, IPathValueStore values, IAclStore acls)
    {
        if (!string.Equals(Hash(content), expectedHash, StringComparison.OrdinalIgnoreCase))
            return new ElevatedResult { Error = "The change file isn't the one PATHology asked to apply, so nothing was written." };

        ElevatedBatch? batch;
        try { batch = JsonSerializer.Deserialize<ElevatedBatch>(content, PathSnapshotJson.Options); }
        catch (JsonException) { batch = null; }
        if (batch is null) return new ElevatedResult { Error = "The change file couldn't be read, so nothing was written." };

        if (Invalid(batch) is { } reason) return new ElevatedResult { Error = reason + " Nothing was written." };

        var steps = new List<StepResult>();
        if (batch.Machine is { } machine)
        {
            steps.Add(Steps.WriteValue(values, machine));
        }
        foreach (var acl in batch.Acls)
        {
            if (steps.Any(s => s.Status == StepStatus.Failed)) { steps.Add(new(acl.Path, StepStatus.Skipped)); continue; }
            steps.Add(Steps.WriteAcl(acls, acl));
        }
        return new ElevatedResult { Steps = steps };
    }

    /// <summary>Why the batch is outside what the helper may do, or null.</summary>
    static string? Invalid(ElevatedBatch batch)
    {
        if (batch.Machine is { } m && (m.Before.Scope != PathScope.Machine || m.After.Scope != PathScope.Machine))
            return "The change file asked the elevated helper to write something other than the machine PATH.";
        if (batch.Acls.FirstOrDefault(a => PathText.Classify(a.Path) != PathForm.Absolute || PathText.Canonical(a.Path) != a.Path) is { } bad)
            return $"The change file names a folder that isn't a plain local path ({bad.Path}).";
        if (batch.Acls.Any(a => a.AfterSddl is null))
            return "The change file has a folder with no permissions to write.";
        return null;
    }
}
