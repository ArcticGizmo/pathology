using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Pathology.App.ViewModels;
using Pathology.Core.Capture;
using static Pathology.App.Theme;

namespace Pathology.App.Scanning;

public enum StepState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>One row of the scan checklist.</summary>
public sealed partial class ScanStepViewModel(CaptureStep step, string label) : ViewModelBase
{
    public CaptureStep Step { get; } = step;
    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DotBrush), nameof(LabelBrush))]
    private StepState _state;

    /// <summary>"12 of 49", for the counted steps.</summary>
    [ObservableProperty] private string? _detail;

    public IBrush DotBrush => State switch
    {
        StepState.Running => Brush("AccentBrush"),
        StepState.Done => Brush("OkBrush"),
        StepState.Failed => Brush("DangerBrush"),
        _ => Brush("BorderBrush"),
    };

    public IBrush LabelBrush => State == StepState.Pending ? Brush("MutedBrush") : Brush("FgBrush");
}

/// <summary>
/// The scan as a checklist: one row per <see cref="CaptureStep"/>, each pending, running or done, with a count
/// for the steps that go folder by folder. Fed from <see cref="CaptureProgress"/> reports.
/// </summary>
public sealed class ScanProgressViewModel : ViewModelBase
{
    public ObservableCollection<ScanStepViewModel> Steps { get; } =
    [
        new(CaptureStep.ReadingRegistry, "Read the machine and user PATH from the registry"),
        new(CaptureStep.ReadingEnvironment, "Build the environment a new process gets"),
        new(CaptureStep.ResolvingPerspectives, "Work out who's who: you, you elevated, SYSTEM, a standard user"),
        new(CaptureStep.ProbingDirectories, "Look at each folder: exists, links, drive, owner, permissions"),
        new(CaptureStep.ListingCommands, "List the commands each folder provides"),
        new(CaptureStep.EvaluatingAccess, "Check who can write to each folder"),
    ];

    public void Reset()
    {
        foreach (var s in Steps)
        {
            s.State = StepState.Pending;
            s.Detail = null;
        }
    }

    /// <summary>Apply one report: earlier steps are done, this one is running.</summary>
    public void Apply(CaptureProgress report)
    {
        if (report.Step == CaptureStep.Done)
        {
            Complete();
            return;
        }
        foreach (var s in Steps)
        {
            if (s.Step < report.Step) s.State = StepState.Done;
            else if (s.Step == report.Step)
            {
                s.State = StepState.Running;
                if (report.Total > 0) s.Detail = $"{report.Completed} of {report.Total}";
            }
        }
    }

    public void Complete()
    {
        foreach (var s in Steps) s.State = StepState.Done;
    }

    /// <summary>The running step failed; the ones never reached stay pending.</summary>
    public void Fail()
    {
        foreach (var s in Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Failed;
    }
}
