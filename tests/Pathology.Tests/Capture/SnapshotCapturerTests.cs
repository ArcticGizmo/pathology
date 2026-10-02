using Pathology.Core.Capture;
using Pathology.Core.Model;

namespace Pathology.Tests.Capture;

public class SnapshotCapturerTests
{
    readonly FakeEnvironment _env = new();
    readonly FakeProbe _probe = new();
    readonly FakeEvaluator _evaluator = new();

    public SnapshotCapturerTests()
    {
        _env.NewProcess["SystemRoot"] = @"C:\Windows";
        _env.NewProcess["USERPROFILE"] = @"C:\Users\you";
        _env.NewProcess["PATHEXT"] = ".COM;.EXE;.BAT";
        _probe.With(@"C:\").With(@"C:\Windows").With(@"C:\Windows\system32").With(@"C:\Tools").With(@"C:\Users\you\bin");
    }

    PathSnapshot Capture(FakeRegistry registry, CaptureOptions? options = null, IProgress<CaptureProgress>? progress = null) =>
        new SnapshotCapturer(registry, _env, new FakeHost(), new FakePerspectives(), _probe, _evaluator)
            .Capture(options, progress);

    [Fact]
    public void Entries_run_machine_then_user_each_in_stored_order()
    {
        var snapshot = Capture(new FakeRegistry(@"%SystemRoot%\system32;C:\Tools\", @"%USERPROFILE%\bin"));

        Assert.Equal(
            [(PathScope.Machine, 0), (PathScope.Machine, 1), (PathScope.User, 0)],
            snapshot.Entries.Select(e => (e.Scope, e.Index)));

        var system32 = snapshot.Entries[0];
        Assert.Equal(@"%SystemRoot%\system32", system32.Raw);
        Assert.Equal(@"C:\Windows\system32", system32.Expanded);
        Assert.Equal(PathForm.Absolute, system32.Form);
        Assert.Equal(["SystemRoot"], system32.Variables);
        Assert.Equal(@"C:\Windows\system32", system32.ProbePath);
        Assert.Equal(@"C:\WINDOWS\SYSTEM32", system32.Key);

        Assert.Equal(@"C:\Tools", snapshot.Entries[1].ProbePath);
        Assert.Equal(@"C:\Users\you\bin", snapshot.Entries[2].ProbePath);
    }

    [Fact]
    public void A_REG_SZ_value_stays_literal_as_Windows_leaves_it()
    {
        var snapshot = Capture(new FakeRegistry(@"%SystemRoot%\system32", machineKind: PathValueKind.String));

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal(entry.Raw, entry.Expanded);
        Assert.Equal(PathForm.Relative, entry.Form);
        Assert.Null(entry.ProbePath);
        Assert.Equal(["SystemRoot"], entry.UnresolvedVariables);
    }

    [Fact]
    public void The_machine_PATH_cannot_see_a_variable_defined_only_for_the_user()
    {
        var registry = new FakeRegistry(@"%TOOLS%\bin", @"%TOOLS%\bin");
        registry.User["TOOLS"] = @"C:\Users\you\tools";
        _env.NewProcess["TOOLS"] = @"C:\Users\you\tools";

        var snapshot = Capture(registry);

        var machine = snapshot.EntriesIn(PathScope.Machine).Single();
        Assert.Equal(@"%TOOLS%\bin", machine.Expanded);
        Assert.Equal(["TOOLS"], machine.UnresolvedVariables);

        var user = snapshot.EntriesIn(PathScope.User).Single();
        Assert.Equal(@"C:\Users\you\tools\bin", user.Expanded);
        Assert.Empty(user.UnresolvedVariables);
    }

    [Fact]
    public void Where_both_scopes_define_a_variable_the_machine_PATH_uses_the_machine_value()
    {
        var registry = new FakeRegistry(@"%TOOLS%", @"%TOOLS%");
        registry.Machine["TOOLS"] = @"%SystemRoot%\tools";
        registry.User["TOOLS"] = @"C:\Users\you\tools";
        _env.NewProcess["TOOLS"] = @"C:\Users\you\tools";

        var snapshot = Capture(registry);

        Assert.Equal(@"C:\Windows\tools", snapshot.EntriesIn(PathScope.Machine).Single().Expanded);
        Assert.Equal(@"C:\Users\you\tools", snapshot.EntriesIn(PathScope.User).Single().Expanded);
    }

    [Fact]
    public void Network_probing_is_off_unless_asked_for()
    {
        var snapshot = Capture(new FakeRegistry(@"\\server\share\bin;C:\Tools"));

        Assert.All(_probe.Calls, c => Assert.False(c.AllowNetwork));
        var unc = snapshot.FactsFor(snapshot.Entries[0])!;
        Assert.Equal(ProbeStatus.SkippedNetwork, unc.Status);
        Assert.Equal(PathForm.Unc, snapshot.Entries[0].Form);
        Assert.False(snapshot.Options.ProbeNetworkPaths);
    }

    [Fact]
    public void Opting_in_passes_through_to_the_probe()
    {
        Capture(new FakeRegistry(@"\\server\share\bin"), new CaptureOptions { ProbeNetworkPaths = true });

        Assert.All(_probe.Calls, c => Assert.True(c.AllowNetwork));
    }

    [Fact]
    public void A_missing_folder_records_its_nearest_existing_ancestor()
    {
        var snapshot = Capture(new FakeRegistry(@"C:\Tools\missing\deeper"));

        var facts = snapshot.FactsFor(snapshot.Entries[0])!;
        Assert.False(facts.Exists);
        Assert.Equal(@"C:\Tools", facts.NearestExistingAncestor);
        // The walk stops at the first folder that exists, and captures it (the phantom check needs its ACL).
        Assert.NotNull(snapshot.FactsFor(@"C:\Tools\missing"));
        Assert.True(snapshot.FactsFor(@"C:\Tools")!.Exists);
        Assert.DoesNotContain(_probe.Calls, c => c.Path == @"C:\");
    }

    [Fact]
    public void A_missing_drive_has_no_ancestor()
    {
        var snapshot = Capture(new FakeRegistry(@"Q:\gone"));

        Assert.Null(snapshot.FactsFor(snapshot.Entries[0])!.NearestExistingAncestor);
    }

    [Fact]
    public void Link_targets_are_captured_alongside_the_link()
    {
        _probe.With(@"C:\Link", f => f with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint, ReparseTag = ReparseTags.MountPoint, ReparseTarget = @"D:\Real\" })
              .With(@"D:\Real");

        var snapshot = Capture(new FakeRegistry(@"C:\Link"));

        Assert.True(snapshot.FactsFor(@"C:\Link")!.IsJunction);
        Assert.True(snapshot.FactsFor(@"D:\Real")!.Exists);
    }

    [Fact]
    public void A_link_loop_ends()
    {
        _probe.With(@"C:\A", f => f with { ReparseTarget = @"C:\B" })
              .With(@"C:\B", f => f with { ReparseTarget = @"C:\A" });

        var snapshot = Capture(new FakeRegistry(@"C:\A"));

        Assert.Equal(1, _probe.Calls.Count(c => c.Path == @"C:\A"));
        Assert.NotNull(snapshot.FactsFor(@"C:\B"));
    }

    [Fact]
    public void Access_is_evaluated_for_every_perspective_wherever_a_descriptor_was_captured()
    {
        var snapshot = Capture(new FakeRegistry(@"C:\Tools;C:\Tools\missing"));

        var tools = snapshot.FactsFor(@"C:\Tools")!;
        Assert.Equal(Enum.GetValues<Perspective>(), tools.Access.Select(a => a.Perspective));
        Assert.Empty(snapshot.FactsFor(@"C:\Tools\missing")!.Access);
    }

    [Fact]
    public void An_evaluator_failure_is_recorded_not_thrown()
    {
        _evaluator.Answer = (_, id) => id.Perspective == Perspective.System ? throw new InvalidOperationException("boom") : new() { Perspective = id.Perspective };

        var snapshot = Capture(new FakeRegistry(@"C:\Tools"));

        var system = snapshot.FactsFor(@"C:\Tools")!.AccessFor(Perspective.System)!;
        Assert.False(system.Evaluated);
        Assert.Equal("boom", system.Error);
    }

    [Fact]
    public void A_probe_failure_is_recorded_not_thrown()
    {
        var snapshot = new SnapshotCapturer(new FakeRegistry(@"C:\Tools"), _env, new FakeHost(), new FakePerspectives(),
            new ThrowingProbe(), _evaluator).Capture();

        Assert.Equal(ProbeStatus.Failed, snapshot.Directories.Single().Status);
    }

    [Fact]
    public void Relative_and_empty_entries_are_not_probed()
    {
        var snapshot = Capture(new FakeRegistry(@"bin;;.;C:tools;\rooted"));

        Assert.All(snapshot.Entries, e => Assert.Null(e.ProbePath));
        Assert.Empty(_probe.Calls);
    }

    [Fact]
    public void Each_folder_is_probed_once_however_it_is_spelled()
    {
        Capture(new FakeRegistry(@"C:\Tools;c:\tools\;C:/Tools", @"""C:\Tools"""));

        Assert.Single(_probe.Calls, c => c.Path.Equals(@"C:\Tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_key_uses_the_long_name_of_an_8_3_path()
    {
        _probe.With(@"C:\PROGRA~1\Tool", f => f with { LongPath = @"C:\Program Files\Tool" });

        var snapshot = Capture(new FakeRegistry(@"C:\PROGRA~1\Tool;C:\Program Files\Tool"));

        Assert.Equal(snapshot.Entries[1].Key, snapshot.Entries[0].Key);
    }

    [Fact]
    public void Only_the_values_PATH_depends_on_are_kept()
    {
        var registry = new FakeRegistry(@"%SystemRoot%", @"%DEV%\bin");
        registry.User["DEV"] = @"%JAVA_HOME%\..";
        registry.User["GITHUB_TOKEN"] = "ghp_not-a-real-token";
        _env.NewProcess["DEV"] = @"C:\Java\..";
        _env.NewProcess["JAVA_HOME"] = @"C:\Java";
        _env.NewProcess["GITHUB_TOKEN"] = "ghp_not-a-real-token";
        _env.Current["GITHUB_TOKEN"] = "ghp_not-a-real-token";

        var snapshot = Capture(registry);

        foreach (var source in snapshot.Environment)
            if (source.Defines("GITHUB_TOKEN"))
                Assert.Null(source.ValueOf("GITHUB_TOKEN"));
        var user = snapshot.EnvironmentFrom(EnvironmentSource.User)!;
        Assert.True(user.Defines("GITHUB_TOKEN"));
        Assert.Equal(@"%JAVA_HOME%\..", user.ValueOf("DEV"));
        // JAVA_HOME matters only through DEV, and is kept for it.
        Assert.Equal(@"C:\Java", snapshot.EnvironmentFrom(EnvironmentSource.NewProcess)!.ValueOf("JAVA_HOME"));
    }

    [Fact]
    public void The_effective_and_process_PATHs_are_kept_side_by_side()
    {
        _env.NewProcess["Path"] = @"C:\Windows;C:\Tools";
        _env.Current["PATH"] = @"C:\Windows";

        var snapshot = Capture(new FakeRegistry(@"C:\Windows;C:\Tools"));

        Assert.Equal(@"C:\Windows;C:\Tools", snapshot.EffectivePath);
        Assert.Equal(@"C:\Windows", snapshot.ProcessPath);
        Assert.Equal(".COM;.EXE;.BAT", snapshot.PathExt);
    }

    [Fact]
    public void Progress_runs_through_every_step_and_finishes()
    {
        var steps = new List<CaptureStep>();
        Capture(new FakeRegistry(@"C:\Tools"), progress: new SyncProgress(p => steps.Add(p.Step)));

        Assert.Equal(Enum.GetValues<CaptureStep>(), steps.Distinct());
    }

    [Fact]
    public void Each_existing_entry_folder_lists_its_command_files_by_PATHEXT_plus_ps1()
    {
        _probe.Files[@"C:\Tools"] = ["tool.exe", "Tool.BAT", "setup.ps1", "readme.txt", "lib.dll"];

        var snapshot = Capture(new FakeRegistry(@"C:\Tools;C:\Tools\missing"));

        Assert.Equal(["tool.exe", "Tool.BAT", "setup.ps1"], snapshot.FactsFor(@"C:\Tools")!.CommandFiles);
        Assert.Null(snapshot.FactsFor(@"C:\Tools\missing")!.CommandFiles);
        // The nearest-ancestor walk found C:\Tools too, but only entry folders are listed, and once each.
        Assert.Single(_probe.Listed);
    }

    [Fact]
    public void Network_and_link_to_network_folders_are_never_listed()
    {
        _probe.With(@"C:\ToShare", f => f with { ReparseTarget = @"\\server\share", ReparseTargetIsNetwork = true });

        Capture(new FakeRegistry(@"\\server\share\bin;C:\ToShare;C:\Tools"));

        Assert.Equal([@"C:\Tools"], _probe.Listed.Select(l => l.Path));
    }

    [Fact]
    public void Without_PATHEXT_the_Windows_default_is_used()
    {
        _env.NewProcess.Remove("PATHEXT");

        Capture(new FakeRegistry(@"C:\Tools"));

        var extensions = Assert.Single(_probe.Listed).Extensions;
        Assert.Contains(".MSC", extensions);
        Assert.Contains(".exe", extensions);   // case-insensitive
        Assert.Contains(".PS1", extensions);
    }

    sealed class ThrowingProbe : IDirectoryProbe
    {
        public DirectoryFacts Probe(string path, bool allowNetwork) => throw new IOException("device error");
        public IReadOnlyList<string>? ListFiles(string path, IReadOnlySet<string> extensions, bool allowNetwork) => throw new IOException("device error");
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; this reports inline so the order is exact.</summary>
    sealed class SyncProgress(Action<CaptureProgress> report) : IProgress<CaptureProgress>
    {
        public void Report(CaptureProgress value) => report(value);
    }
}
