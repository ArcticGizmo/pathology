using Pathology.App.Scanning;
using Pathology.App.ViewModels;
using Pathology.Core.Model;

namespace Pathology.Tests.App;

/// <summary>Records where a page tried to send you.</summary>
internal sealed class RecordingNavigator : INavigator
{
    public List<object> Calls { get; } = [];

    public void ToFindings(FindingsQuery query) => Calls.Add(query);
    public void ToEntry(PathScope scope, int index) => Calls.Add((scope, index));
    public void ToLearn(string topic) => Calls.Add("learn:" + topic);
    public void ToCommand(string command) => Calls.Add("command:" + command);
    public void ToFix(PathScope scope, int index) => Calls.Add(("fix", scope, index));
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
