using Pathology.Core.Detection;
using Pathology.Core.Model;

namespace Pathology.Tests.Detection;

public class CorrectnessDetectorTests
{
    const string Windows = @"%SystemRoot%\system32;%SystemRoot%";

    // COR-01 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR01_the_machine_PATH_using_a_user_only_variable_is_high()
    {
        var machine = new TestMachine($@"{Windows};%TOOLS%\bin");
        machine.Registry.User["TOOLS"] = @"C:\Users\you\tools";
        machine.Environment.NewProcess["TOOLS"] = @"C:\Users\you\tools";

        var finding = machine.Single("COR-01");

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("only your user environment defines", finding.Title);
        Assert.Contains(@"%TOOLS%\bin", finding.What);
        // A literal %VAR% is relative too, but COR-03 leaves it to COR-01.
        Assert.Empty(machine.Findings("COR-03"));
    }

    [Fact]
    public void COR01_a_variable_nothing_defines_is_high_in_either_scope()
    {
        var finding = new TestMachine(Windows, @"%NOPE%\bin").Single("COR-01");

        Assert.Equal(PathScope.User, finding.Scope);
        Assert.Contains("isn't defined", finding.Title);
    }

    [Fact]
    public void COR01_a_machine_variable_is_fine()
    {
        var machine = new TestMachine($@"{Windows};%TOOLS%\bin").Folder(@"C:\Tools\bin");
        machine.Registry.Machine["TOOLS"] = @"C:\Tools";
        machine.Environment.NewProcess["TOOLS"] = @"C:\Tools";

        Assert.Empty(machine.Findings("COR-01"));
    }

    // COR-02 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR02_variables_in_a_REG_SZ_value_are_one_finding_for_the_value()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;%SystemRoot%;C:\Tools", machineKind: PathValueKind.String);

        var finding = machine.Single("COR-02");

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(2, finding.Entries.Count);
        Assert.Contains("REG_EXPAND_SZ", finding.Fix);
        Assert.Empty(machine.Findings("COR-03"));
    }

    [Fact]
    public void COR02_REG_EXPAND_SZ_is_fine()
    {
        Assert.Empty(new TestMachine(Windows).Findings("COR-02"));
    }

    // COR-03 ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(".", "is the current directory itself")]
    [InlineData("bin", "is looked up under the current directory")]
    [InlineData("C:tools", "tools under drive C:'s current directory")]
    [InlineData(@"\tools", "at the root of whichever drive is current")]
    public void COR03_relative_entries_are_high(string entry, string meaning)
    {
        var finding = new TestMachine($"{Windows};{entry}").Single("COR-03");

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains(meaning, finding.What);
    }

    [Fact]
    public void COR03_empty_entries_are_one_medium_finding_per_value()
    {
        var finding = new TestMachine($";{Windows};;C:\\Windows\\System32\\Wbem").Folder(@"C:\Windows\System32\Wbem").Single("COR-03");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal(2, finding.Entries.Count);
        Assert.Contains("#1 and #4", finding.What);
    }

    [Fact]
    public void COR03_leaves_the_trailing_semicolon_Windows_writes_itself()
    {
        Assert.Empty(new TestMachine(Windows, @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps;").Findings("COR-03"));
    }

    // COR-04 ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\you\tools", "your profile")]
    [InlineData(@"C:\Users\sam\tools", "another user's profile")]
    [InlineData(@"%USERPROFILE%\tools", "%USERPROFILE%")]
    public void COR04_profile_folders_in_the_machine_PATH_are_medium(string entry, string expected)
    {
        var finding = new TestMachine($"{Windows};{entry}").Single("COR-04");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains(expected, finding.What);
    }

    [Fact]
    public void COR04_the_public_profile_and_the_user_PATH_are_fine()
    {
        Assert.Empty(new TestMachine($@"{Windows};C:\Users\Public\tools", @"C:\Users\you\tools").Findings("COR-04"));
    }

    // COR-05 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR05_missing_folders_and_files_are_dead()
    {
        var machine = new TestMachine($@"{Windows};C:\Program Files\Gone;C:\Program Files\tool.exe")
            .Folder(@"C:\Program Files")
            .Folder(@"C:\Program Files\tool.exe", f => f.Shape(d => d with { IsDirectory = false, Attributes = FileAttributes.Archive }));

        var findings = machine.Findings("COR-05");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(Severity.Low, f.Severity));
        Assert.Contains(findings, f => f.Title.EndsWith("doesn't exist"));
        Assert.Contains(findings, f => f.Title.EndsWith("is a file, not a folder"));
    }

    [Fact]
    public void COR05_leaves_network_paths_it_never_looked_at_alone()
    {
        Assert.Empty(new TestMachine($@"{Windows};\\server\share\bin").Findings("COR-05"));
    }

    // COR-06 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR06_the_same_folder_spelled_differently_is_a_duplicate_and_the_first_wins()
    {
        var machine = new TestMachine($@"{Windows};C:\Tools;c:\tools\", @"""C:\Tools""").Folder(@"C:\Tools");

        var finding = machine.Single("COR-06");

        Assert.Equal(3, finding.Entries.Count);
        Assert.Contains("Keep C:\\Tools (machine #3)", finding.Fix);
        Assert.Contains("does nothing at all", finding.Why);
        Assert.Null(finding.Scope);
    }

    [Fact]
    public void COR06_distinct_folders_are_not_duplicates()
    {
        Assert.Empty(new TestMachine(Windows).Findings("COR-06"));
    }

    // COR-07 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR07_competing_folders_are_grouped_by_the_pair_of_folders()
    {
        var machine = new TestMachine($@"{Windows};C:\Python312;C:\Python311")
            .Folder(@"C:\Python312", f => f.Files("python.exe", "pip.exe"))
            .Folder(@"C:\Python311", f => f.Files("python.exe", "pip.exe", "idle.bat"));

        var finding = machine.Single("COR-07");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(@"C:\Python312 hides 2 commands that C:\Python311 also has", finding.Title);
        Assert.Contains(@"python: C:\Python312\python.exe wins over C:\Python311\python.exe", finding.Evidence);
    }

    [Fact]
    public void COR07_commands_only_one_folder_provides_compete_with_nothing()
    {
        var machine = new TestMachine($@"{Windows};C:\Python312;C:\Node")
            .Folder(@"C:\Python312", f => f.Files("python.exe"))
            .Folder(@"C:\Node", f => f.Files("node.exe"));

        Assert.Empty(machine.Findings("COR-07"));
    }

    [Fact]
    public void COR07_two_extensions_of_a_name_in_one_folder_are_not_competing_folders()
    {
        // PATHEXT decides between them inside the folder; no other folder is hidden.
        var machine = new TestMachine($@"{Windows};C:\Tools")
            .Folder(@"C:\Tools", f => f.Files("build.exe", "build.cmd"));

        Assert.Empty(machine.Findings("COR-07"));
        Assert.Equal("build.exe", machine.Diagnose().Shadows.Resolve("build")!.Winner.FileName);
    }

    // COR-08 ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1500, null)]
    [InlineData(1700, Severity.Medium)]
    [InlineData(2000, Severity.High)]
    [InlineData(2500, Severity.High)]
    public void COR08_the_2047_character_threshold(int length, Severity? expected)
    {
        var machine = new TestMachine(Windows) { EffectivePath = new string('x', length) };

        var findings = machine.Findings("COR-08").Where(f => f.Subject == "2,047").ToList();

        Assert.Equal(expected, findings.SingleOrDefault()?.Severity);
    }

    // COR-09 ------------------------------------------------------------------------------------------------

    [Fact]
    public void COR09_no_machine_PATH_at_all_is_high()
    {
        var finding = new TestMachine(null!, @"C:\Users\you\bin").Single("COR-09");

        Assert.Equal(Severity.High, finding.Severity);
        Assert.StartsWith("There's no machine PATH", finding.Title);
        Assert.Equal("no-system32", finding.RootCause);
    }

    [Fact]
    public void COR09_a_PATH_that_names_the_Windows_folders_is_quiet()
    {
        Assert.Empty(new TestMachine(Windows).Findings("COR-09"));
        // Spelled with %windir% instead of %SystemRoot%, it's still System32.
        Assert.Empty(new TestMachine(@"%windir%\System32").Findings("COR-09"));
    }

    [Fact]
    public void COR09_a_REG_SZ_machine_PATH_is_one_problem_with_COR02()
    {
        var diagnosis = new TestMachine(Windows, machineKind: PathValueKind.String).Diagnose();

        var group = Assert.Single(diagnosis.Groups, g => g.Members.Any(f => f.Rule == "COR-09"));
        Assert.Equal("COR-02", group.Primary.Rule);
        Assert.Contains("unexpanded text", group.Members.Single(f => f.Rule == "COR-09").Title);
    }

    [Fact]
    public void COR08_a_value_of_exactly_1024_characters_looks_like_setx()
    {
        var value = @"C:\Windows\system32;" + new string('a', 1024 - 20);
        var finding = new TestMachine(Windows, value).Findings("COR-08").Single(f => f.RootCause.StartsWith("setx"));

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(PathScope.User, finding.Scope);
    }
}

public class HygieneAndConfigurationDetectorTests
{
    const string Windows = @"%SystemRoot%\system32;%SystemRoot%";

    [Theory]
    [InlineData(@"""C:\Program Files\Tool""", "HYG-01")]
    [InlineData(@" C:\Tool", "HYG-02")]
    [InlineData(@"C:\Tool ", "HYG-02")]
    [InlineData(@"C:\Tool\\bin", "HYG-03")]
    [InlineData(@"C:/Tool/bin", "HYG-03")]
    public void Each_hygiene_defect_is_a_low_finding_for_its_value(string entry, string rule)
    {
        var finding = new TestMachine(Windows, entry).Single(rule);

        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal(FindingCategory.Hygiene, finding.Category);
        Assert.Equal(PathScope.User, finding.Scope);
    }

    [Fact]
    public void Hygiene_findings_list_every_affected_entry_once()
    {
        var finding = new TestMachine(Windows, @"""C:\A"";""C:\B"";C:\C").Single("HYG-01");

        Assert.Equal("2 entries in your user PATH contain quotes", finding.Title);
        Assert.Equal(2, finding.Entries.Count);
    }

    [Fact]
    public void HYG04_a_mix_of_trailing_backslashes_is_information_only()
    {
        Assert.Equal(Severity.Info, new TestMachine($@"{Windows};C:\Tools\").Single("HYG-04").Severity);
        Assert.Empty(new TestMachine(@"C:\A\;C:\B\").Findings("HYG-04"));
    }

    [Fact]
    public void HYG04_a_drive_root_is_not_a_trailing_backslash()
    {
        Assert.Empty(new TestMachine($@"{Windows};D:\").Findings("HYG-04"));
    }

    [Fact]
    public void A_clean_PATH_has_no_hygiene_findings()
    {
        Assert.DoesNotContain(new TestMachine(Windows, @"C:\Users\you\bin").Diagnose().Findings, f => f.Category == FindingCategory.Hygiene);
    }

    [Fact]
    public void CFG01_a_new_process_PATH_that_differs_from_the_registry_is_medium_with_a_diff()
    {
        var machine = new TestMachine(Windows) { EffectivePath = @"C:\Windows\system32;C:\Windows;C:\Injected" };

        var finding = machine.Single("CFG-01");

        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains(@"+ C:\Injected (only in the new-process PATH)", finding.Evidence);
    }

    [Fact]
    public void CFG01_Windows_dropping_a_repeated_entry_is_not_a_difference()
    {
        // A user entry that repeats a machine one never reaches a new process.
        var machine = new TestMachine(Windows, @"C:\Windows") { EffectivePath = @"C:\Windows\system32;C:\Windows" };

        Assert.Empty(machine.Findings("CFG-01"));
    }

    [Fact]
    public void CFG01_one_variable_holding_several_entries_matches_its_expansion()
    {
        var machine = new TestMachine(@"%SystemRoot%\system32;%TOOLS%") { EffectivePath = @"C:\Windows\system32;C:\A;C:\B" };
        machine.Environment.NewProcess["TOOLS"] = @"C:\A;C:\B";
        machine.Registry.Machine["TOOLS"] = @"C:\A;C:\B";

        Assert.Empty(machine.Findings("CFG-01"));
    }

    [Fact]
    public void CFG02_a_process_started_with_the_current_PATH_is_quiet()
    {
        Assert.Empty(new TestMachine(Windows, @"C:\Users\you\bin").Findings("CFG-02"));
    }

    [Fact]
    public void CFG02_the_same_entries_in_another_order_still_differ()
    {
        var machine = new TestMachine(Windows, @"C:\Users\you\bin") { ProcessPath = @"C:\Users\you\bin;C:\Windows\system32;C:\Windows" };

        Assert.Contains("The same entries, in a different order", machine.Single("CFG-02").Evidence);
    }

    [Fact]
    public void CFG02_a_process_started_with_an_older_PATH_is_information()
    {
        var machine = new TestMachine(Windows, @"C:\Users\you\bin") { ProcessPath = @"C:\Windows\system32;C:\Windows" };

        var finding = machine.Single("CFG-02");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains(@"- C:\Users\you\bin (only in a new process's PATH)", finding.Evidence);
    }
}
