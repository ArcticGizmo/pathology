using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.App.ViewModels;

/// <summary>What the Findings page should show when another page sends you there.</summary>
/// <param name="Category">Only this category, or every one.</param>
/// <param name="NotesOnly">Only the notes (Info findings).</param>
/// <param name="RootCause">Select this group.</param>
public sealed record FindingsQuery(FindingCategory? Category = null, bool NotesOnly = false, string? RootCause = null);

/// <summary>Jumps between pages: Health to a finding, a finding to its entry or its Learn article, and so on.</summary>
public interface INavigator
{
    void ToFindings(FindingsQuery query);

    void ToEntry(PathScope scope, int index);

    void ToLearn(string topic);

    /// <summary>Open Shadowing with a command looked up.</summary>
    void ToCommand(string command);

    /// <summary>Open Fix with this entry picked out in the editor.</summary>
    void ToFix(PathScope scope, int index);

    /// <summary>Open Fix with this machine entry moved to the user PATH in the editor, ready to review and apply.</summary>
    void ToFixMovingToUser(int machineIndex);
}
