using Pathology.Core.Capture;
using Pathology.Core.Detection;
using Pathology.Core.Remediation;
using Pathology.Core.Store;
using Pathology.Windows;

namespace Pathology.App.Repair;

/// <summary>
/// What the Fix and History pages need from the machine. Designing and projecting are read-only; <see cref="Apply"/>
/// is the one call that writes, and it's only ever reached from a click on Apply or Undo.
/// </summary>
/// <remarks>
/// Injected into the shell, so the headless renderer and the tests hand the pages a stand-in whose
/// <see cref="Apply"/> refuses. Only <c>App</c> passes the real one (<see cref="AppServices.Repair"/>).
/// </remarks>
public interface IRepairService
{
    /// <summary>Designs folder lock-downs (pure).</summary>
    IAclDesigner Designer { get; }

    /// <summary>What a change set would leave behind (read-only; may probe a folder the scan never saw).</summary>
    PlanOutcome Project(Diagnosis before, ChangeSet changes);

    /// <summary>Every apply, newest first.</summary>
    IReadOnlyList<ChangeRecord> History();

    /// <summary>The change set that undoes a record, against the live state (read-only).</summary>
    ChangeSet Undo(ChangeRecord record);

    /// <summary><b>Writes.</b> Backs up, applies (one UAC prompt if needed), verifies and records.</summary>
    ChangeRecord Apply(ChangeSet changes, string summary, IReadOnlyList<string> fixes, Guid? undoOf = null);
}

/// <summary>The real machine's repair service. Built only by <see cref="AppServices"/>.</summary>
internal sealed class MachineRepair(IPathologyPaths paths, IAccessEvaluator evaluator) : IRepairService
{
    readonly RegistryPathValueStore _values = new();
    readonly SecurityDescriptorStore _acls = new();
    readonly FileChangeHistory _history = new(paths);
    readonly DirectoryProbe _probe = new();

    public IAclDesigner Designer { get; } = new AclDesigner();

    public PlanOutcome Project(Diagnosis before, ChangeSet changes) => PlanProjection.Project(before, changes, evaluator, _probe);

    public IReadOnlyList<ChangeRecord> History() => _history.All();

    public ChangeSet Undo(ChangeRecord record) => record.Undo(_values.Read, path =>
    {
        try { return _acls.Read(path); }
        catch (Exception) { return null; }
    });

    public ChangeRecord Apply(ChangeSet changes, string summary, IReadOnlyList<string> fixes, Guid? undoOf = null)
    {
        var helper = new ElevatedHelperLauncher(
            Environment.ProcessPath ?? throw new InvalidOperationException("PATHology can't find its own exe to elevate."),
            paths.PendingDirectory, _values, _acls);
        return new ChangeApplier(_values, _acls, new EnvironmentBroadcast(), helper, _history).Apply(changes, summary, fixes, undoOf);
    }
}
