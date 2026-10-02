using Pathology.App.Scanning;
using Pathology.App.ViewModels;
using Pathology.Core.Model;

namespace Pathology.Tests.App;

/// <summary>Records where a page tried to send you.</summary>
internal sealed class RecordingNavigator : INavigator
{
    public List<object> Calls { get; } = [];

    public void ToEntry(PathScope scope, int index) => Calls.Add((scope, index));
    public void ToEntries(PathScope scope) => Calls.Add(scope);
    public void ToLearn(string topic) => Calls.Add("learn:" + topic);
    public void ToCommand(string command) => Calls.Add("command:" + command);
    public void ToReview() => Calls.Add("review");
}

/// <summary>The staging model and both entry pages over one session, the way the shell wires them.</summary>
internal sealed class EntryPages
{
    public EntryPages(ScanSession? session = null)
    {
        Session = session ?? Sessions.Showing(Pathology.App.Rendering.PosedMachines.Messy());
        Pending = new PendingChanges(Session, Repair);
        System = new EntriesViewModel(PathScope.Machine, Session, Navigator, Pending);
        User = new EntriesViewModel(PathScope.User, Session, Navigator, Pending);
    }

    public ScanSession Session { get; }
    public RecordingRepair Repair { get; } = new();
    public RecordingNavigator Navigator { get; } = new();
    public PendingChanges Pending { get; }
    public EntriesViewModel System { get; }
    public EntriesViewModel User { get; }

    /// <summary>The live (not struck-through) line whose text is this.</summary>
    public static EntryRowViewModel Line(EntriesViewModel page, string text) =>
        page.Rows.Single(r => !r.IsGhost && r.Text == text);
}

internal static class Sessions
{
    /// <summary>A session showing a result, whose capture fails the test if anything tries to scan.</summary>
    public static ScanSession Showing(PathSnapshot snapshot)
    {
        var session = NeverScans();
        session.Show(ScanResult.Of(snapshot));
        return session;
    }

    public static ScanSession NeverScans() =>
        new((_, _) => throw new InvalidOperationException("a page test must never scan"));
}
