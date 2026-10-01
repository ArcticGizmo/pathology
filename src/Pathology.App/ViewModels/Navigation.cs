using Pathology.Core.Model;

namespace Pathology.App.ViewModels;

/// <summary>Jumps between pages: the Dashboard to an entry, an entry's problem to its Learn article, and so on.</summary>
public interface INavigator
{
    /// <summary>Open the System or User page with this scanned entry picked out.</summary>
    void ToEntry(PathScope scope, int index);

    /// <summary>Open the System or User page as it is.</summary>
    void ToEntries(PathScope scope);

    void ToLearn(string topic);

    /// <summary>Open Shadowing with a command looked up.</summary>
    void ToCommand(string command);

    /// <summary>Open Review: what's staged, what it changes, and Apply.</summary>
    void ToReview();
}
