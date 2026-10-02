using Pathology.App.Repair;
using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.App.Rendering;

/// <summary>
/// The repair service the renderer (and tests) hand the shell: it designs and projects as the real one does, over a
/// posed machine, and <b>refuses to apply or undo</b>. Posed folders have made-up SDDL, so the designer describes
/// a typical lock-down rather than parsing it, and a designed folder evaluates as locked.
/// </summary>
internal sealed class PosedRepair(IReadOnlyList<ChangeRecord>? history = null) : IRepairService
{
    public IAclDesigner Designer { get; } = new PosedDesigner();

    public PlanOutcome Project(Diagnosis before, ChangeSet changes) => PlanProjection.Project(before, changes, new LockedEvaluator());

    public IReadOnlyList<ChangeRecord> History() => history ?? [];

    public ChangeSet Undo(ChangeRecord record) => throw new InvalidOperationException("The renderer never undoes anything.");

    public ChangeRecord Apply(ChangeSet changes, string summary, IReadOnlyList<string> fixes, Guid? undoOf = null) =>
        throw new InvalidOperationException("The renderer never applies anything.");

    sealed class PosedDesigner : IAclDesigner
    {
        public AclDesign Design(string path, string sddl, AclFix fix, AclDesignMode mode)
        {
            var lines = new List<string>();
            var commands = new List<string>();
            var q = $"\"{path}\"";
            if (fix.StripWrite)
            {
                switch (mode)
                {
                    case AclDesignMode.Protect:
                        lines.Add("Inheritance: on → off (what was inherited is kept as the folder's own, then trimmed)");
                        lines.Add("Authenticated Users: modify → read & execute");
                        commands.Add($"icacls {q} /inheritance:d");
                        commands.Add($"icacls {q} /grant:r *S-1-5-11:(OI)(CI)RX");
                        break;
                    case AclDesignMode.OwnEntriesOnly:
                        lines.Add("Users: modify → read & execute");
                        commands.Add($"icacls {q} /grant:r *S-1-5-32-545:(OI)(CI)RX");
                        break;
                    default:
                        lines.Add("Authenticated Users: modify → read & execute (through inheritance)");
                        break;
                }
            }
            if (fix.ResetOwner)
            {
                lines.Add("Owner: you → Administrators");
                commands.Add($"icacls {q} /setowner *S-1-5-32-544");
            }
            return new AclDesign
            {
                Sddl = sddl.Replace("S:(PATH:", "S:(LOCKED:", StringComparison.Ordinal),
                OwnerSid = fix.ResetOwner ? WellKnownSids.Administrators : null,
                Lines = lines,
                Commands = commands,
            };
        }
    }

    /// <summary>A designed (posed) folder: nobody but administrators can write it.</summary>
    sealed class LockedEvaluator : IAccessEvaluator
    {
        public AccessResult Evaluate(string sddl, PerspectiveIdentity identity) => new()
        {
            Perspective = identity.Perspective,
            Granted = identity.Perspective is Perspective.System or Perspective.CurrentUserElevated
                ? FileAccessRights.ListDirectory | FileAccessRights.AddFile | FileAccessRights.AddSubdirectory | FileAccessRights.WriteDac
                : FileAccessRights.ListDirectory | FileAccessRights.Traverse,
        };
    }

    /// <summary>A history worth looking at: a lock-down that went through, one cancelled at the UAC prompt, and an undo.</summary>
    public static IReadOnlyList<ChangeRecord> PosedHistory()
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 42, 0, TimeSpan.Zero);
        var before = new RawPathValue { Scope = PathScope.Machine, Kind = PathValueKind.ExpandString, Value = @"C:\Tools;%SystemRoot%\system32;%SystemRoot%;C:\OldApp\bin" };
        var after = before with { Value = @"C:\Tools;%SystemRoot%\system32;%SystemRoot%" };
        var acl = new AclChange
        {
            Path = @"C:\Tools", Scope = PathScope.Machine, Mode = AclDesignMode.Protect, BeforeSddl = "O:BAD:AI", AfterSddl = "O:BAD:PAI",
            NeedsAdmin = true,
            Lines = ["Inheritance: on → off (what was inherited is kept as the folder's own, then trimmed)", "Users: modify → read & execute"],
        };
        var applied = new ChangeRecord
        {
            Id = Guid.Parse("0f8a3c1e-5d2b-4f7a-9c6e-1b2d3e4f5a6b"), StartedAt = at, FinishedAt = at.AddSeconds(4),
            Summary = "2 fixes", Fixes = [@"Lock down C:\Tools", @"Remove C:\OldApp\bin from the machine PATH"],
            Changes = new ChangeSet { Values = [new ValueChange { Before = before, After = after }], Acls = [acl] },
            Outcome = ChangeOutcome.Applied,
            Steps = [new(StepResult.MachineTarget, StepStatus.Applied), new(@"C:\Tools", StepStatus.Applied)],
        };
        var cancelled = applied with
        {
            Id = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"), StartedAt = at.AddMinutes(-20), FinishedAt = at.AddMinutes(-20),
            Summary = "1 fix", Fixes = [@"Lock down C:\Tools"], Outcome = ChangeOutcome.Cancelled,
            Message = "The UAC prompt was declined, so nothing was changed.",
            Steps = [new(@"C:\Tools", StepStatus.Skipped)],
            Changes = new ChangeSet { Acls = [acl] },
        };
        var user = new RawPathValue { Scope = PathScope.User, Kind = PathValueKind.ExpandString, Value = "\"C:\\Users\\you\\bin\";C:\\Users\\you\\scoop\\shims " };
        var tidied = user with { Value = @"C:\Users\you\bin;C:\Users\you\scoop\shims" };
        var undone = new ChangeRecord
        {
            Id = Guid.Parse("2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e"), StartedAt = at.AddDays(-1), FinishedAt = at.AddDays(-1),
            Summary = "2 fixes", Fixes = ["Remove the quotes from 1 entry in your user PATH", "Trim the spaces from 1 entry in your user PATH"],
            Changes = new ChangeSet { Values = [new ValueChange { Before = user, After = tidied }] },
            Outcome = ChangeOutcome.Applied, Steps = [new(StepResult.UserTarget, StepStatus.Applied)],
            UndoneBy = Guid.Parse("3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f"),
        };
        var undo = undone with
        {
            Id = undone.UndoneBy!.Value, StartedAt = at.AddDays(-1).AddMinutes(5), UndoOf = undone.Id, UndoneBy = null,
            Summary = "Undo of 2 fixes", Fixes = [],
            Changes = new ChangeSet { Values = [new ValueChange { Before = tidied, After = user }] },
        };
        return [applied, cancelled, undo, undone];
    }
}
