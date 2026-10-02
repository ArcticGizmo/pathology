using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Tests.Detection;

public class ShadowEngineTests
{
    static TestMachine Toolchains() => new TestMachine(@"%SystemRoot%\system32;C:\Tools;C:\Old", @"C:\Users\you\bin")
        .Folder(@"C:\Tools", f => f.Files("tool.exe", "tool.bat", "where.bat", "only.ps1"))
        .Folder(@"C:\Old", f => f.Files("tool.com", "legacy.cmd", "only.ps1"))
        .Folder(@"C:\Users\you\bin", f => f.Files("mine.exe", "tool.exe").WritableByYou());

    [Fact]
    public void An_earlier_folder_wins_whatever_the_extension()
    {
        var tool = Diagnoser.Diagnose(Toolchains().Snapshot()).Shadows.Resolve("tool")!;

        // C:\Tools comes before C:\Old, so its .exe beats C:\Old's .com even though .COM is first in PATHEXT.
        Assert.Equal(@"C:\Tools\tool.exe", tool.Winner.FullPath);
        Assert.Equal([@"C:\Tools\tool.bat", @"C:\Old\tool.com", @"C:\Users\you\bin\tool.exe"], tool.Hidden.Select(h => h.FullPath));
        Assert.Equal(2, tool.HiddenElsewhere.Count());
    }

    [Fact]
    public void Within_a_folder_PATHEXT_order_decides()
    {
        var tool = Diagnoser.Diagnose(Toolchains().Snapshot()).Shadows.Resolve("tool")!;

        Assert.Equal(".EXE", tool.Winner.Extension);
        Assert.Equal(".BAT", tool.Hidden[0].Extension);
    }

    [Fact]
    public void A_name_with_an_extension_matches_only_that_file()
    {
        var shadows = Diagnoser.Diagnose(Toolchains().Snapshot()).Shadows;

        Assert.Equal(@"C:\Old\tool.com", shadows.Resolve("tool.com")!.Winner.FullPath);
        Assert.Equal(@"C:\Users\you\bin\tool.exe", shadows.Resolve("TOOL.EXE")!.Hidden.Single().FullPath);
        Assert.Null(shadows.Resolve("tool.vbs"));
        Assert.Null(shadows.Resolve("nothing"));
    }

    [Fact]
    public void Built_ins_and_writable_winners_are_marked()
    {
        var shadows = Diagnoser.Diagnose(Toolchains().Snapshot()).Shadows;

        Assert.True(shadows.Resolve("where")!.Winner.IsWindows);
        Assert.Contains("where", shadows.Builtins.Select(b => b.Command));
        Assert.True(shadows.Resolve("mine")!.Winner.WritableByYou);
        Assert.False(shadows.Resolve("mine")!.Winner.WritableByOthers);
    }

    [Fact]
    public void PowerShell_differences_are_notes()
    {
        var shadows = Diagnoser.Diagnose(Toolchains().Snapshot()).Shadows;

        // .ps1 isn't in PATHEXT, so cmd never runs only.ps1 and it isn't a command here at all.
        Assert.Null(shadows.Resolve("only"));
        Assert.Contains(shadows.Resolve("where")!.Notes, n => n.Contains("alias for Where-Object"));
    }

    [Fact]
    public void Competing_lists_only_commands_hidden_in_another_folder_riskiest_first()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;C:\Open;C:\Tools")
            .Folder(@"C:\Open", f => f.Files("a.exe").WritableByEveryone())
            .Folder(@"C:\Tools", f => f.Files("a.exe", "b.exe", "b.bat", "z.exe"))
            .Folder(@"C:\Windows\System32", f => f.Files("z.exe"));

        var competing = Diagnoser.Diagnose(machine.Snapshot()).Shadows.Competing.Select(c => c.Command).ToList();

        // b is hidden only by its own .exe beside it, so it doesn't compete.
        Assert.Equal(["a", "z"], competing);
    }

    [Fact]
    public void Folders_that_couldnt_be_listed_are_reported()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;\\server\share\bin");

        Assert.Equal([@"\\server\share\bin"], Diagnoser.Diagnose(machine.Snapshot()).Shadows.UnlistedFolders);
    }
}

public class DiagnoserTests
{
    [Fact]
    public void Every_rule_is_registered_once_with_a_stable_ID()
    {
        var rules = Diagnoser.All.Select(d => d.Rule).ToList();

        Assert.Equal(rules.Distinct().Count(), rules.Count);
        Assert.Equal(25, rules.Count);
        Assert.All(rules, r => Assert.Matches(@"^(SEC|COR|HYG|CFG)-\d\d$", r));
    }

    [Fact]
    public void A_stock_Windows_PATH_has_nothing_worse_than_information()
    {
        // What a fresh install looks like: locked Windows folders, and the per-user WindowsApps folder.
        var machine = new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SYSTEMROOT%\System32\WindowsPowerShell\v1.0\;%SYSTEMROOT%\System32\OpenSSH\",
                @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;")
            .Folder(@"C:\Windows\System32\Wbem")
            .Folder(@"C:\Windows\System32\WindowsPowerShell\v1.0")
            .Folder(@"C:\Windows\System32\OpenSSH")
            .Folder(@"C:\Users\you\AppData\Local\Microsoft\WindowsApps", f => f.WritableByYou());

        var diagnosis = machine.Diagnose();

        Assert.All(diagnosis.Findings, f => Assert.True(f.Severity == Severity.Info, $"{f.Rule}: {f.Title}"));
    }

    [Fact]
    public void Findings_are_ranked_worst_first_and_each_group_is_led_by_its_worst()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;C:\Gone;C:\Tools\;bin")
            .Folder(@"C:\", f => f.FoldersCreatableByEveryone())
            .Folder(@"C:\Tools");

        var diagnosis = machine.Diagnose();

        Assert.Equal(diagnosis.Findings.OrderByDescending(f => f.Severity).Select(f => f.Severity), diagnosis.Findings.Select(f => f.Severity));
        Assert.All(diagnosis.Groups, g => Assert.True(g.Members.All(m => m.Severity <= g.Primary.Severity)));
        // SEC-03 and COR-05 are one problem: fewer groups than findings.
        Assert.True(diagnosis.Groups.Count < diagnosis.Findings.Count);
    }

    [Fact]
    public void A_finding_key_is_its_rule_and_subject()
    {
        var finding = new TestMachine(@"%SystemRoot%\system32;C:\Tools").Folder(@"C:\Tools", f => f.WritableByEveryone()).Single("SEC-01");

        Assert.Equal(@"SEC-01|C:\Tools", finding.Key);
    }

    [Fact]
    public void Diagnosing_is_pure()
    {
        var snapshot = new TestMachine(@"%SystemRoot%\system32;C:\Tools;C:\Gone").Folder(@"C:\Tools", f => f.WritableByEveryone()).Snapshot();

        var first = Diagnoser.Diagnose(snapshot).Findings.Select(f => (f.Key, f.Severity, f.What));
        var second = Diagnoser.Diagnose(snapshot).Findings.Select(f => (f.Key, f.Severity, f.What));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_redacted_snapshot_diagnoses_like_the_original()
    {
        var snapshot = new TestMachine(@"%SystemRoot%\system32;C:\Users\you\tools")
            .Folder(@"C:\Users\you\tools", f => f.WritableByYou())
            .Snapshot();

        var original = Diagnoser.Diagnose(snapshot).Findings.Select(f => (f.Rule, f.Severity)).ToList();
        var redacted = Diagnoser.Diagnose(Core.Redaction.SnapshotRedactor.Redact(snapshot)).Findings.Select(f => (f.Rule, f.Severity)).ToList();

        Assert.Contains(("COR-04", Severity.Medium), redacted);
        Assert.Equal(original, redacted);
    }
}
