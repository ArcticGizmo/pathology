using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Redaction;
using Pathology.Tests.Detection;

namespace Pathology.Tests.Redaction;

public class NamePseudonymiserTests
{
    /// <summary>A PC with private script names, a shadowed built-in, and two copies of one tool.</summary>
    static TestMachine Machine()
    {
        var m = new TestMachine(@"C:\Tools;%SystemRoot%\system32;C:\Other", @"C:\Users\you\scripts")
            .Folder(@"C:\Tools", f => f.WritableByEveryone().Files("where.bat", "jq.exe"))
            .Folder(@"C:\Other", f => f.Files("jq.exe", "acme.deploy.bat"))
            .Folder(@"C:\Users\you\scripts", f => f.WritableByYou().Files("payroll.api.bat", "ACME.DEPLOY.cmd"));
        m.Environment.NewProcess["ACME_TOKEN_FILE"] = @"C:\secret";
        m.Registry.User["ACME_TOKEN_FILE"] = @"C:\secret";
        return m;
    }

    static IEnumerable<string> Files(PathSnapshot s, string folder) => s.FactsFor(folder)!.CommandFiles!;

    [Fact]
    public void Private_command_names_become_placeholders_and_keep_their_extension()
    {
        var redacted = SnapshotRedactor.Redact(Machine().Snapshot());
        var json = PathSnapshotJson.Serialize(redacted);

        Assert.DoesNotContain("payroll", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acme.deploy", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(Files(redacted, @"C:\Users\you\scripts".Replace("you", SnapshotRedactor.UserPlaceholder)),
            f => Assert.Matches(@"^<cmd-\d+>\.(bat|cmd)$", f));
    }

    [Fact]
    public void The_same_name_gets_the_same_placeholder_everywhere_whatever_its_case()
    {
        var redacted = SnapshotRedactor.Redact(Machine().Snapshot());

        var jqTools = Files(redacted, @"C:\Tools").Single(f => f.EndsWith(".exe"));
        var jqOther = Files(redacted, @"C:\Other").Single(f => f.EndsWith(".exe"));
        Assert.Equal(jqTools, jqOther);

        var deploy = Files(redacted, @"C:\Other").Single(f => f.EndsWith(".bat"));
        var deployUser = Files(redacted, $@"C:\Users\{SnapshotRedactor.UserPlaceholder}\scripts").Single(f => f.EndsWith(".cmd"));
        Assert.Equal(Path.GetFileNameWithoutExtension(deploy), Path.GetFileNameWithoutExtension(deployUser));
    }

    [Fact]
    public void Windows_command_names_stay_readable_including_a_copy_that_shadows_one()
    {
        var redacted = SnapshotRedactor.Redact(Machine().Snapshot());

        Assert.Contains("where.exe", Files(redacted, @"C:\Windows\System32"));
        Assert.Contains("where.bat", Files(redacted, @"C:\Tools"));
    }

    [Fact]
    public void The_diagnosis_is_the_same_after_redaction()
    {
        var snapshot = Machine().Snapshot();
        var before = Diagnoser.Diagnose(snapshot);
        var after = Diagnoser.Diagnose(SnapshotRedactor.Redact(snapshot));

        Assert.Equal(before.Findings.Select(f => (f.Rule, f.Severity)), after.Findings.Select(f => (f.Rule, f.Severity)));
        Assert.Equal(before.Shadows.Competing.Count(), after.Shadows.Competing.Count());
        Assert.Equal("where", after.Shadows.Resolve("where")!.Command);
    }

    [Fact]
    public void Variable_names_path_does_not_depend_on_are_pseudonymised()
    {
        var redacted = SnapshotRedactor.Redact(Machine().Snapshot());
        var names = redacted.Environment.SelectMany(e => e.Variables.Keys).ToList();

        Assert.DoesNotContain("ACME_TOKEN_FILE", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(names, n => n.StartsWith("<var-"));
        // PATH's own variables, and Windows' well-known ones, keep their names.
        Assert.Contains("SystemRoot", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("PATHEXT", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_variable_a_path_entry_names_keeps_its_name_even_when_undefined()
    {
        var m = new TestMachine(@"%SystemRoot%\system32;%MISSING_SDK%\bin");
        var redacted = SnapshotRedactor.Redact(m.Snapshot());

        Assert.Equal(
            Diagnoser.Diagnose(m.Snapshot()).Findings.Select(f => f.Rule),
            Diagnoser.Diagnose(redacted).Findings.Select(f => f.Rule));
        Assert.Contains(redacted.Entries, e => e.Raw.Contains("%MISSING_SDK%"));
    }

    [Fact]
    public void Pseudonymising_twice_changes_nothing()
    {
        var once = SnapshotRedactor.Redact(Machine().Snapshot());
        var twice = SnapshotRedactor.Redact(once);

        Assert.Equal(PathSnapshotJson.Serialize(once), PathSnapshotJson.Serialize(twice));
    }
}
