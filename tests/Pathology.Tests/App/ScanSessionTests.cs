using Pathology.App.Rendering;
using Pathology.App.Scanning;
using Pathology.Core.Capture;

namespace Pathology.Tests.App;

public class ScanSessionTests
{
    [Fact]
    public async Task A_scan_publishes_its_result_and_the_checklist_ends_done()
    {
        var snapshot = PosedMachines.Clean();
        var session = new ScanSession((progress, _) =>
        {
            progress.Report(new CaptureProgress(CaptureStep.ProbingDirectories, 1, 2));
            return snapshot;
        });

        await session.ScanAsync();

        Assert.Same(snapshot, session.Current?.Snapshot);
        Assert.False(session.IsScanning);
        Assert.Null(session.Error);
        Assert.All(session.Progress.Steps, s => Assert.Equal(StepState.Done, s.State));
    }

    [Fact]
    public async Task A_failed_scan_keeps_the_last_result_and_says_why()
    {
        var calls = 0;
        var session = new ScanSession((_, _) => ++calls == 1 ? PosedMachines.Clean() : throw new IOException("registry unreadable"));
        await session.ScanAsync();
        var first = session.Current;

        await session.ScanAsync();

        Assert.Same(first, session.Current);
        Assert.Equal("registry unreadable", session.Error);
        Assert.False(session.IsScanning);
    }

    [Fact]
    public void Progress_marks_earlier_steps_done_and_counts_the_current_one()
    {
        var progress = new ScanProgressViewModel();

        progress.Apply(new CaptureProgress(CaptureStep.ProbingDirectories, 17, 41));

        var steps = progress.Steps.ToDictionary(s => s.Step);
        Assert.Equal(StepState.Done, steps[CaptureStep.ReadingRegistry].State);
        Assert.Equal(StepState.Done, steps[CaptureStep.ResolvingPerspectives].State);
        Assert.Equal(StepState.Running, steps[CaptureStep.ProbingDirectories].State);
        Assert.Equal("17 of 41", steps[CaptureStep.ProbingDirectories].Detail);
        Assert.Equal(StepState.Pending, steps[CaptureStep.EvaluatingAccess].State);

        progress.Fail();
        Assert.Equal(StepState.Failed, steps[CaptureStep.ProbingDirectories].State);
        Assert.Equal(StepState.Pending, steps[CaptureStep.EvaluatingAccess].State);
    }
}
