using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Pathology.App.Changelog;
using Pathology.App.Scanning;
using Pathology.App.ViewModels;
using Pathology.App.Views;
using Pathology.Core.Capture;
using Pathology.Core.Changelog;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.App.Rendering;

/// <summary>
/// Renders the app's views to PNG on a headless Skia platform, so the UI can be eyeballed without a
/// display. Invoked via <c>pathology render &lt;dir&gt;</c> (by convention <c>./captures/render</c>).
/// </summary>
/// <remarks>
/// <b>Read-only by construction.</b> It builds real view-models over a throwaway temp store and a scan session
/// that can't scan: every page shows a <see cref="PosedMachines">posed</see> made-up machine handed to the session
/// directly, never this one. When a page needs a state to render interestingly (an update available, a scan
/// part-way through) that state is set on the view-model, never produced by running the operation.
/// </remarks>
internal static class HeadlessRenderer
{
    public static int RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            // A temp store root, so a render never reads or writes the real settings file.
            var root = Path.Combine(Path.GetTempPath(), "pathology-render-" + Guid.NewGuid().ToString("N"));
            try
            {
                var services = new AppServices(root);
                var messy = ScanResult.Of(PosedMachines.Messy());

                var vm = Shell(services, messy);
                // Pose each page with something worth looking at selected, the way a user would leave it.
                vm.Shadowing.Show("python");
                vm.SystemEntries.Select(PathScope.Machine, 0);
                vm.Learn.Open(Core.Detection.LearnTopics.ValueKinds);
                foreach (var page in vm.Pages)
                {
                    vm.CurrentPage = page;
                    Capture(vm, Path.Combine(outDir, $"main_{page.Title.ToLowerInvariant()}.png"));
                }

                RenderDashboardStates(outDir, services);
                RenderSelections(outDir, services, messy);
                RenderRepair(outDir, services, messy);
                RenderChangelog(outDir);
                RenderUpdateButton(outDir, services, messy);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            }

            Console.WriteLine($"rendered to {Path.GetFullPath(outDir)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>A session that refuses to scan: the renderer only ever shows posed results.</summary>
    static ScanSession PosedSession(ScanResult? result)
    {
        var session = new ScanSession((_, _) => throw new InvalidOperationException("the renderer never scans"));
        if (result is not null) session.Show(result);
        return session;
    }

    /// <summary>The shell over a posed result, with a repair service that refuses to write and a posed history.</summary>
    static MainWindowViewModel Shell(AppServices services, ScanResult? result) =>
        new(services, PosedSession(result), new PosedRepair(PosedRepair.PosedHistory()), checkForUpdates: false);

    /// <summary>
    /// The Dashboard when everything is clean, part-way through a scan, and before any scan; then the worst case and
    /// an empty PATH on every page drawn from the scan.
    /// </summary>
    static void RenderDashboardStates(string outDir, AppServices services)
    {
        Capture(Shell(services, ScanResult.Of(PosedMachines.Clean())), Path.Combine(outDir, "dashboard_clean.png"));

        // Part-way through: the checklist is posed from a progress report; nothing runs.
        var scanning = Shell(services, null);
        scanning.Session.IsScanning = true;
        scanning.Session.Progress.Apply(new CaptureProgress(CaptureStep.ProbingDirectories, 17, 41));
        Capture(scanning, Path.Combine(outDir, "dashboard_scanning.png"));

        Capture(Shell(services, null), Path.Combine(outDir, "dashboard_idle.png"));

        // The worst case and the empty one, across the pages where they look different.
        foreach (var (name, snapshot) in new[] { ("worst", PosedMachines.Worst()), ("empty", PosedMachines.Empty()) })
        {
            var vm = Shell(services, ScanResult.Of(snapshot));
            foreach (var page in new PageViewModel[] { vm.Dashboard, vm.SystemEntries, vm.UserEntries, vm.Shadowing })
            {
                vm.CurrentPage = page;
                Capture(vm, Path.Combine(outDir, $"{page.Title.ToLowerInvariant()}_{name}.png"));
            }
        }
    }

    /// <summary>Other selections worth eyeballing: a problem unfolded, a hygiene entry, a shadowed built-in, a long article.</summary>
    static void RenderSelections(string outDir, AppServices services, ScanResult result)
    {
        var vm = Shell(services, result);

        // A problem with no fix, unfolded to say what to do (a pose: it only toggles a flag).
        if (vm.Dashboard.WorthKnowing.FirstOrDefault() is { } item) item.IsExpanded = true;
        Capture(vm, Path.Combine(outDir, "dashboard_unfolded.png"), height: 1300);

        vm.ToEntry(PathScope.User, 2);
        Capture(vm, Path.Combine(outDir, "user_quotes.png"));

        // A missing folder: a phantom-directory risk as well as dead.
        var phantom = result.Diagnosis.Groups.First(g => g.Members.Any(f => f.Rule == "SEC-03"));
        var entry = phantom.Members.SelectMany(f => f.Entries).First();
        vm.ToEntry(entry.Scope, entry.Index);
        Capture(vm, Path.Combine(outDir, "system_phantom.png"));

        vm.ToCommand("where");
        Capture(vm, Path.Combine(outDir, "shadowing_where.png"));

        vm.ToLearn(Core.Detection.LearnTopics.DllSearchOrder);
        Capture(vm, Path.Combine(outDir, "learn_dll.png"));
    }

    /// <summary>
    /// Staging, Review and History in the states worth checking: the recommended fixes staged, hand edits, a move to
    /// the user PATH, a reorder that changes which command runs, the confirm step, a finished apply, an empty history
    /// and an undo being confirmed. Every state is posed on the view-models; the repair service would throw if
    /// anything tried to apply.
    /// </summary>
    static void RenderRepair(string outDir, AppServices services, ScanResult result)
    {
        var staged = Shell(services, result);
        staged.Pending.StageRecommended();
        staged.ToEntry(PathScope.Machine, 0);
        Capture(staged, Path.Combine(outDir, "system_staged.png"));
        staged.ToReview();
        Capture(staged, Path.Combine(outDir, "review_staged.png"), height: 2000);
        staged.Review.IsConfirming = true;
        Capture(staged, Path.Combine(outDir, "review_confirm.png"), height: 2000);

        var order = Shell(services, result);
        order.Pending.Fixes.Single(f => f.Fix.Id == RemediationPlanner.WindowsFirstId).IsStaged = true;
        order.ToReview();
        Capture(order, Path.Combine(outDir, "review_windows_first.png"), height: 1400);

        // Hand edits: one deleted, one moved up, one moved over from the system PATH, one being edited.
        var edited = Shell(services, result);
        var user = edited.UserEntries;
        user.Rows.Last(r => !r.IsGhost).DeleteCommand.Execute(null);
        user.Rows.Where(r => !r.IsGhost).ElementAt(1).MoveUpCommand.Execute(null);
        edited.SystemEntries.Rows.Last(r => !r.IsGhost).MoveScopeCommand.Execute(null);
        edited.CurrentPage = user;
        user.Rows.First(r => !r.IsGhost).BeginEditCommand.Execute(null);
        Capture(edited, Path.Combine(outDir, "user_edited.png"));

        // "Move to your user PATH" on its own: the user PATH gets the copy first.
        var move = Shell(services, result);
        move.ToEntry(PathScope.Machine, 0);
        move.SystemEntries.Selected!.MoveScopeCommand.Execute(null);
        Capture(move, Path.Combine(outDir, "system_move_to_user.png"));
        move.ToReview();
        Capture(move, Path.Combine(outDir, "review_move_to_user.png"));

        var done = Shell(services, result);
        done.ToReview();
        done.Review.PoseOutcome(PosedRepair.PosedHistory()[0]);
        Capture(done, Path.Combine(outDir, "review_applied.png"));

        var clean = Shell(services, ScanResult.Of(PosedMachines.Clean()));
        clean.CurrentPage = clean.SystemEntries;
        Capture(clean, Path.Combine(outDir, "system_clean.png"));

        var history = Shell(services, result);
        history.CurrentPage = history.History;
        history.History.Selected = history.History.Rows[2];
        history.History.IsConfirming = true;
        Capture(history, Path.Combine(outDir, "history_undo.png"));

        var empty = new MainWindowViewModel(services, PosedSession(result), new PosedRepair(), checkForUpdates: false);
        empty.CurrentPage = empty.History;
        Capture(empty, Path.Combine(outDir, "history_empty.png"));
    }

    /// <summary>The "what's new" window (the post-update popup / About viewer), over the real changelog.</summary>
    static void RenderChangelog(string outDir)
    {
        var markdown = ChangelogMarkdown.LoadEmbedded();
        var sections = markdown is null ? [] : ChangelogParser.Parse(markdown);
        var window = new ChangelogWindow("What's new in PATHology", "Recent releases", sections);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "changelog.png"));
        window.Close();
    }

    /// <summary>
    /// The "Update to v…" call-to-action in the nav, posed with a version so it's visible: a dev or headless
    /// build is never Velopack-installed, so a real check reports "not applicable" and the button hides. The
    /// version is set directly — a read-only pose, nothing is downloaded or applied.
    /// </summary>
    static void RenderUpdateButton(string outDir, AppServices services, ScanResult result)
    {
        var vm = Shell(services, result);
        vm.AvailableVersion = "0.2.0";
        Capture(vm, Path.Combine(outDir, "update_available.png"));
    }

    static void Capture(MainWindowViewModel vm, string path, double? height = null)
    {
        var window = new MainWindow { DataContext = vm };
        if (height is { } h) window.Height = h;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(path);
        window.Close();
    }
}
