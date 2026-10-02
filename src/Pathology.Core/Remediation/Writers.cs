using System.Text.Json.Serialization;
using Pathology.Core.Model;

namespace Pathology.Core.Remediation;

// The machine-facing writers. Real implementations live in Pathology.Windows and are built only by the
// composition root (AppServices) and the elevated helper's verb; tests use fakes. See CLAUDE.md: nothing writes
// the PATH, the registry or an ACL except production code driven by a real click.

/// <summary>Reads and writes a stored PATH value, raw and with its kind.</summary>
public interface IPathValueStore
{
    /// <summary>The value as stored now, unexpanded.</summary>
    RawPathValue Read(PathScope scope);

    /// <summary>Store the value exactly, with its kind. <see cref="PathValueKind.Missing"/> deletes it.</summary>
    void Write(RawPathValue value);
}

/// <summary>Reads and writes a folder's permissions as SDDL.</summary>
public interface IAclStore
{
    /// <summary>The folder's security descriptor now, in the form a scan captures it.</summary>
    string Read(string path);

    /// <summary>
    /// True when <paramref name="actual"/> has the same owner, the same inheritance setting and the same entries of
    /// its own as <paramref name="expected"/>. Inherited entries are left out: they follow the parent, and a parent
    /// locked down earlier in the same apply changes them.
    /// </summary>
    bool SameOwnPermissions(string expected, string actual);

    /// <summary>Write a designed descriptor (owner and DACL), then read it back and throw if it isn't what was asked.</summary>
    void Write(string path, string sddl);
}

/// <summary>Tells running programs (Explorer first) that the environment changed.</summary>
public interface IEnvironmentBroadcast
{
    /// <summary>Broadcast <c>WM_SETTINGCHANGE</c> "Environment". False when it couldn't be sent.</summary>
    bool Broadcast();
}

/// <summary>Runs the part of a change that needs an administrator, behind one UAC prompt.</summary>
public interface IElevatedRunner
{
    ElevatedResult Run(ElevatedBatch batch);
}

/// <summary>What happened to one write.</summary>
public enum StepStatus
{
    Applied,
    Failed,

    /// <summary>Not attempted, because an earlier step failed or the UAC prompt was declined.</summary>
    Skipped,
}

/// <summary>One write's outcome.</summary>
/// <param name="Target">"machine" or "user" for a PATH value, else the folder.</param>
public sealed record StepResult(string Target, StepStatus Status, string? Message = null)
{
    public const string MachineTarget = "machine";
    public const string UserTarget = "user";

    public static string TargetOf(PathScope scope) => scope == PathScope.Machine ? MachineTarget : UserTarget;
}

/// <summary>The writes the elevated helper makes: the machine PATH and the folders that need an administrator.</summary>
public sealed record ElevatedBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ValueChange? Machine { get; init; }
    public IReadOnlyList<AclChange> Acls { get; init; } = [];

    [JsonIgnore] public bool IsEmpty => Machine is null && Acls.Count == 0;

    /// <summary>The part of a change set that needs an administrator.</summary>
    public static ElevatedBatch Of(ChangeSet changes) => new()
    {
        Id = changes.Id,
        Machine = changes.ValueFor(PathScope.Machine),
        Acls = changes.Acls.Where(a => a.Writes && a.NeedsAdmin).ToList(),
    };
}

/// <summary>What the elevated helper reported.</summary>
public sealed record ElevatedResult
{
    /// <summary>The UAC prompt was declined: nothing ran.</summary>
    public bool Cancelled { get; init; }

    public IReadOnlyList<StepResult> Steps { get; init; } = [];

    /// <summary>Why the helper didn't run or didn't report back, or null.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The writes themselves, shared by the app and the elevated helper so both check and verify the same way: is it
/// still what the scan saw, write it, read it back.
/// </summary>
public static class Steps
{
    public static StepResult WriteValue(IPathValueStore store, ValueChange change)
    {
        var target = StepResult.TargetOf(change.Scope);
        try
        {
            if (!Same(store.Read(change.Scope), change.Before))
                return new(target, StepStatus.Failed, "It changed after the scan, so it was left alone. Re-scan and try again.");
            store.Write(change.After);
            return Same(store.Read(change.Scope), change.After)
                ? new(target, StepStatus.Applied)
                : new(target, StepStatus.Failed, "It was written, but didn't read back as written.");
        }
        catch (Exception ex)
        {
            return new(target, StepStatus.Failed, ex.Message);
        }
    }

    public static StepResult WriteAcl(IAclStore store, AclChange change)
    {
        try
        {
            if (change.AfterSddl is not { } sddl) return new(change.Path, StepStatus.Skipped, "Nothing was designed for it.");
            if (!store.SameOwnPermissions(change.BeforeSddl, store.Read(change.Path)))
                return new(change.Path, StepStatus.Failed, "Its permissions changed after the scan, so they were left alone. Re-scan and try again.");
            store.Write(change.Path, sddl);
            return new(change.Path, StepStatus.Applied);
        }
        catch (Exception ex)
        {
            return new(change.Path, StepStatus.Failed, ex.Message);
        }
    }

    /// <summary>The same stored value: text and kind, exactly.</summary>
    public static bool Same(RawPathValue a, RawPathValue b) =>
        a.Kind == b.Kind && (a.Kind == PathValueKind.Missing || string.Equals(a.Value, b.Value, StringComparison.Ordinal));
}
