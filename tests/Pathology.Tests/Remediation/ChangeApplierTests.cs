using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.Remediation;

public class ChangeApplierTests
{
    static readonly RawPathValue MachineBefore = new() { Scope = PathScope.Machine, Value = @"C:\Tools;%SystemRoot%\system32", Kind = PathValueKind.ExpandString };
    static readonly RawPathValue MachineAfter = MachineBefore with { Value = @"%SystemRoot%\system32" };
    static readonly RawPathValue UserBefore = new() { Scope = PathScope.User, Value = "\"C:\\Users\\you\\bin\"", Kind = PathValueKind.String };
    static readonly RawPathValue UserAfter = UserBefore with { Value = @"C:\Users\you\bin" };

    sealed class Rig
    {
        public FakeValues Values { get; } = new();
        public FakeAcls Acls { get; } = new();
        public FakeBroadcast Broadcast { get; } = new();
        public MemoryHistory History { get; } = new();
        public FakeElevation Elevation { get; }
        public ChangeApplier Applier { get; }

        public Rig()
        {
            Values.Stored[PathScope.Machine] = MachineBefore;
            Values.Stored[PathScope.User] = UserBefore;
            Acls.Sddl[@"C:\Tools"] = "O:BAD:(A;;FA;;;BU)";
            Acls.Sddl[@"C:\Users\you\shared"] = "O:BAD:(A;;FA;;;WD)";
            Elevation = new FakeElevation(Values, Acls);
            Applier = new ChangeApplier(Values, Acls, Broadcast, Elevation, History);
        }
    }

    static AclChange Acl(string path, string before, bool admin) => new()
    {
        Path = path, BeforeSddl = before, AfterSddl = before + "locked", Lines = ["locked"], NeedsAdmin = admin,
        Scope = admin ? PathScope.Machine : PathScope.User,
    };

    static ChangeSet Everything() => new()
    {
        Values = [new ValueChange { Before = MachineBefore, After = MachineAfter }, new ValueChange { Before = UserBefore, After = UserAfter }],
        Acls = [Acl(@"C:\Tools", "O:BAD:(A;;FA;;;BU)", admin: true), Acl(@"C:\Users\you\shared", "O:BAD:(A;;FA;;;WD)", admin: false)],
    };

    [Fact]
    public void Applies_every_write_reads_it_back_and_tells_Explorer()
    {
        var rig = new Rig();
        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Applied, record.Outcome);
        Assert.All(record.Steps, s => Assert.Equal(StepStatus.Applied, s.Status));
        Assert.Equal(4, record.Steps.Count);
        Assert.Equal(MachineAfter, rig.Values.Stored[PathScope.Machine]);
        Assert.Equal(UserAfter, rig.Values.Stored[PathScope.User]);
        Assert.Equal("O:BAD:(A;;FA;;;BU)locked", rig.Acls.Sddl[@"C:\Tools"]);
        Assert.Equal(1, rig.Broadcast.Count);
        Assert.Equal(ChangeOutcome.Applied, rig.History.Get(record.Id)!.Outcome);
    }

    [Fact]
    public void The_administrator_part_goes_to_the_helper_first_and_only_it_does()
    {
        var rig = new Rig();
        var order = new List<PathScope>();
        rig.Values.BeforeWrite = v => order.Add(v.Scope);

        rig.Applier.Apply(Everything(), "4 fixes");

        var batch = Assert.Single(rig.Elevation.Batches);
        Assert.Equal(PathScope.Machine, batch.Machine!.Scope);
        Assert.Equal([@"C:\Tools"], batch.Acls.Select(a => a.Path));
        Assert.Equal([PathScope.Machine, PathScope.User], order);
    }

    [Fact]
    public void The_backup_is_on_disk_before_the_first_write()
    {
        var rig = new Rig();
        var changes = Everything();
        rig.Values.BeforeWrite = _ =>
        {
            var saved = Assert.Single(rig.History.Records.Values);
            Assert.Equal(ChangeOutcome.Pending, saved.Outcome);
            Assert.Equal(MachineBefore, saved.Changes.ValueFor(PathScope.Machine)!.Before);
        };

        rig.Applier.Apply(changes, "4 fixes");

        Assert.Equal(ChangeOutcome.Pending, rig.History.Saves[0]);
    }

    [Fact]
    public void A_declined_UAC_prompt_changes_nothing()
    {
        var rig = new Rig();
        rig.Elevation.Decline = true;

        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Cancelled, record.Outcome);
        Assert.Empty(rig.Values.Writes);
        Assert.Empty(rig.Acls.Writes);
        Assert.Equal(0, rig.Broadcast.Count);
        Assert.All(record.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.False(record.CanUndo);
    }

    [Fact]
    public void Anything_changed_since_the_scan_refuses_the_lot_and_leaves_no_record()
    {
        var rig = new Rig();
        rig.Values.Stored[PathScope.User] = UserBefore with { Value = @"C:\Something\Else" };

        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Refused, record.Outcome);
        Assert.Contains("Your user PATH", record.Message);
        Assert.Empty(rig.Elevation.Batches);
        Assert.Empty(rig.Values.Writes);
        Assert.Empty(rig.History.Records);
    }

    [Fact]
    public void A_folder_whose_permissions_changed_refuses_the_lot()
    {
        var rig = new Rig();
        rig.Acls.Sddl[@"C:\Tools"] = "O:BAD:(A;;FA;;;WD)";

        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Refused, record.Outcome);
        Assert.Contains(@"C:\Tools", record.Message);
        Assert.Empty(rig.Values.Writes);
    }

    [Fact]
    public void A_write_that_does_not_read_back_fails_and_stops_the_rest()
    {
        var rig = new Rig();
        rig.Values.Garble = true;

        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Failed, record.Outcome);
        Assert.Equal(StepStatus.Failed, record.Steps.Single(s => s.Target == StepResult.MachineTarget).Status);
        Assert.Equal(StepStatus.Skipped, record.Steps.Single(s => s.Target == @"C:\Tools").Status);
        Assert.Equal(StepStatus.Skipped, record.Steps.Single(s => s.Target == StepResult.UserTarget).Status);
        Assert.Single(rig.Values.Writes);
    }

    [Fact]
    public void A_failed_folder_after_applied_values_is_partial_and_still_broadcast()
    {
        var rig = new Rig();
        rig.Acls.FailFor = @"C:\Users\you\shared";

        var record = rig.Applier.Apply(Everything(), "4 fixes");

        Assert.Equal(ChangeOutcome.Partial, record.Outcome);
        Assert.Contains("denied", record.Steps.Single(s => s.Target == @"C:\Users\you\shared").Message);
        Assert.Equal(1, rig.Broadcast.Count);
        Assert.True(record.CanUndo);
    }

    [Fact]
    public void A_value_past_the_limit_is_refused_before_anything_happens()
    {
        var rig = new Rig();
        var changes = new ChangeSet
        {
            Values = [new ValueChange { Before = UserBefore, After = UserBefore with { Value = new string('x', ChangeSet.MaxValueLength + 1) } }],
        };

        var record = rig.Applier.Apply(changes, "1 fix");

        Assert.Equal(ChangeOutcome.Refused, record.Outcome);
        Assert.Empty(rig.Values.Writes);
    }

    [Fact]
    public void Undo_puts_back_what_was_applied_and_marks_the_original()
    {
        var rig = new Rig();
        var applied = rig.Applier.Apply(Everything(), "4 fixes");

        var undo = applied.Undo(rig.Values.Read, rig.Acls.Read);
        var record = rig.Applier.Apply(undo, "Undo of 4 fixes", undoOf: applied.Id);

        Assert.Equal(ChangeOutcome.Applied, record.Outcome);
        Assert.Equal(MachineBefore, rig.Values.Stored[PathScope.Machine]);
        Assert.Equal(UserBefore, rig.Values.Stored[PathScope.User]);
        Assert.Equal("O:BAD:(A;;FA;;;BU)", rig.Acls.Sddl[@"C:\Tools"]);
        Assert.Equal(record.Id, rig.History.Get(applied.Id)!.UndoneBy);
        Assert.False(rig.History.Get(applied.Id)!.CanUndo);
        Assert.True(rig.History.Get(record.Id)!.CanUndo);
    }

    [Fact]
    public void Undo_reverses_only_what_was_applied()
    {
        var rig = new Rig();
        rig.Acls.FailFor = @"C:\Users\you\shared";
        var applied = rig.Applier.Apply(Everything(), "4 fixes");

        var undo = applied.Undo(rig.Values.Read, rig.Acls.Read);

        Assert.Equal(2, undo.Values.Count);
        Assert.Equal([@"C:\Tools"], undo.Acls.Select(a => a.Path));
    }

    /// <summary><c>C:\Tools</c> moving from the machine PATH to the front of the user PATH.</summary>
    static ChangeSet MoveToolsToUser() => new()
    {
        Values =
        [
            new ValueChange { Before = MachineBefore, After = MachineAfter },
            new ValueChange { Before = UserBefore, After = UserBefore with { Value = "C:\\Tools;\"C:\\Users\\you\\bin\"" } },
        ],
    };

    [Fact]
    public void A_move_to_the_user_PATH_writes_the_copy_before_the_machine_PATH_lets_it_go()
    {
        var rig = new Rig();
        var order = new List<PathScope>();
        rig.Values.BeforeWrite = v => order.Add(v.Scope);

        var record = rig.Applier.Apply(MoveToolsToUser(), "1 edit");

        Assert.Equal(ChangeOutcome.Applied, record.Outcome);
        Assert.Equal([PathScope.User, PathScope.Machine], order);
        Assert.Equal(MachineAfter, rig.Values.Stored[PathScope.Machine]);
        Assert.StartsWith(@"C:\Tools;", rig.Values.Stored[PathScope.User].Value);
        Assert.Equal(2, record.Steps.Count);
    }

    [Fact]
    public void A_declined_UAC_prompt_during_a_move_puts_the_user_PATH_back()
    {
        var rig = new Rig();
        rig.Elevation.Decline = true;

        var record = rig.Applier.Apply(MoveToolsToUser(), "1 edit");

        Assert.Equal(ChangeOutcome.Cancelled, record.Outcome);
        Assert.Equal(UserBefore, rig.Values.Stored[PathScope.User]);
        Assert.Equal(MachineBefore, rig.Values.Stored[PathScope.Machine]);
        Assert.Equal(2, rig.Values.Writes.Count);
        Assert.All(record.Steps, s => Assert.Equal(StepStatus.Skipped, s.Status));
        Assert.False(record.CanUndo);
    }

    [Fact]
    public void A_machine_write_that_fails_during_a_move_leaves_the_entry_in_both()
    {
        var rig = new Rig();
        // Something else changes the machine PATH between the two writes, so the helper leaves it alone.
        rig.Values.BeforeWrite = v =>
        {
            if (v.Scope == PathScope.User) rig.Values.Stored[PathScope.Machine] = MachineBefore with { Value = MachineBefore.Value + @";C:\New" };
        };

        var record = rig.Applier.Apply(MoveToolsToUser(), "1 edit");

        Assert.Equal(ChangeOutcome.Partial, record.Outcome);
        Assert.StartsWith(@"C:\Tools;", rig.Values.Stored[PathScope.User].Value);
        Assert.StartsWith(@"C:\Tools;", rig.Values.Stored[PathScope.Machine].Value);
        Assert.Equal(StepStatus.Failed, record.Steps.Single(s => s.Target == StepResult.MachineTarget).Status);
        Assert.True(record.CanUndo);
    }

    [Fact]
    public void Moves_both_ways_keep_the_upward_entry_in_the_user_PATH_until_the_machine_PATH_has_it()
    {
        var rig = new Rig();
        var userWrites = new List<string?>();
        rig.Values.BeforeWrite = v => { if (v.Scope == PathScope.User) userWrites.Add(v.Value); };
        var changes = new ChangeSet
        {
            Values =
            [
                new ValueChange { Before = MachineBefore, After = MachineBefore with { Value = "%SystemRoot%\\system32;\"C:\\Users\\you\\bin\"" } },
                new ValueChange { Before = UserBefore, After = UserBefore with { Value = @"C:\Tools" } },
            ],
        };

        var record = rig.Applier.Apply(changes, "2 edits");

        Assert.Equal(ChangeOutcome.Applied, record.Outcome);
        Assert.Equal(["C:\\Tools;\"C:\\Users\\you\\bin\"", @"C:\Tools"], userWrites);
        Assert.Equal(@"C:\Tools", rig.Values.Stored[PathScope.User].Value);
    }

    [Fact]
    public void Only_a_move_to_the_user_PATH_changes_the_order()
    {
        Assert.Null(Everything().UserFirst());
        // The user PATH already has it: the machine copy just goes.
        Assert.Null(new ChangeSet
        {
            Values =
            [
                new ValueChange { Before = MachineBefore, After = MachineAfter },
                new ValueChange { Before = UserBefore with { Value = @"c:\tools\" }, After = UserBefore with { Value = @"c:\tools\;C:\X" } },
            ],
        }.UserFirst());
        Assert.Equal(MoveToolsToUser().ValueFor(PathScope.User)!.After, MoveToolsToUser().UserFirst());
    }

    [Fact]
    public void Undoing_a_move_to_the_user_PATH_puts_it_back_in_the_machine_PATH()
    {
        var rig = new Rig();
        var applied = rig.Applier.Apply(MoveToolsToUser(), "1 edit");

        var record = rig.Applier.Apply(applied.Undo(rig.Values.Read, rig.Acls.Read), "Undo of 1 edit", undoOf: applied.Id);

        Assert.Equal(ChangeOutcome.Applied, record.Outcome);
        Assert.Equal(MachineBefore, rig.Values.Stored[PathScope.Machine]);
        Assert.Equal(UserBefore, rig.Values.Stored[PathScope.User]);
    }

    [Fact]
    public void Nothing_to_change_is_refused()
    {
        Assert.Equal(ChangeOutcome.Refused, new Rig().Applier.Apply(new ChangeSet(), "nothing").Outcome);
    }
}

public class ElevatedApplierTests
{
    static readonly RawPathValue Before = new() { Scope = PathScope.Machine, Value = @"C:\A", Kind = PathValueKind.ExpandString };

    static (FakeValues, FakeAcls) Stores()
    {
        var values = new FakeValues();
        values.Stored[PathScope.Machine] = Before;
        return (values, new FakeAcls());
    }

    [Fact]
    public void A_batch_that_does_not_match_its_hash_writes_nothing()
    {
        var (values, acls) = Stores();
        var bytes = ElevatedApplier.Serialize(new ElevatedBatch { Machine = new ValueChange { Before = Before, After = Before with { Value = @"C:\Evil;C:\A" } } });

        var result = ElevatedApplier.Run(bytes, ElevatedApplier.Hash([.. bytes, 0x20]), values, acls);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Steps);
        Assert.Empty(values.Writes);
    }

    [Fact]
    public void The_helper_never_writes_the_user_PATH()
    {
        var (values, acls) = Stores();
        var user = new RawPathValue { Scope = PathScope.User, Value = "x", Kind = PathValueKind.ExpandString };
        var bytes = ElevatedApplier.Serialize(new ElevatedBatch { Machine = new ValueChange { Before = user, After = user with { Value = "y" } } });

        var result = ElevatedApplier.Run(bytes, ElevatedApplier.Hash(bytes), values, acls);

        Assert.Contains("other than the machine PATH", result.Error);
        Assert.Empty(values.Writes);
    }

    [Theory]
    [InlineData(@"\\server\share\tools")]
    [InlineData(@"tools")]
    [InlineData(@"C:\Tools\..\Windows")]
    public void The_helper_refuses_a_folder_that_is_not_a_plain_local_path(string path)
    {
        var (values, acls) = Stores();
        var bytes = ElevatedApplier.Serialize(new ElevatedBatch
        {
            Acls = [new AclChange { Path = path, BeforeSddl = "a", AfterSddl = "b", Lines = ["x"], NeedsAdmin = true }],
        });

        var result = ElevatedApplier.Run(bytes, ElevatedApplier.Hash(bytes), values, acls);

        Assert.Contains("isn't a plain local path", result.Error);
        Assert.Empty(acls.Writes);
    }

    [Fact]
    public void A_good_batch_is_applied()
    {
        var (values, acls) = Stores();
        var bytes = ElevatedApplier.Serialize(new ElevatedBatch { Machine = new ValueChange { Before = Before, After = Before with { Value = @"C:\B" } } });

        var result = ElevatedApplier.Run(bytes, ElevatedApplier.Hash(bytes), values, acls);

        Assert.Null(result.Error);
        Assert.Equal(StepStatus.Applied, Assert.Single(result.Steps).Status);
        Assert.Equal(@"C:\B", values.Stored[PathScope.Machine].Value);
    }
}

public class FileChangeHistoryTests
{
    [Fact]
    public void Records_round_trip_newest_first()
    {
        using var store = new TempStore();
        var history = new FileChangeHistory(store.Paths);
        var value = new RawPathValue { Scope = PathScope.User, Value = @"C:\A", Kind = PathValueKind.String };
        var older = new ChangeRecord
        {
            Id = Guid.NewGuid(), StartedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), Summary = "older",
            Changes = new ChangeSet { Values = [new ValueChange { Before = value, After = value with { Value = @"C:\B" } }] },
            Outcome = ChangeOutcome.Applied, Steps = [new StepResult(StepResult.UserTarget, StepStatus.Applied)],
        };
        var newer = older with { Id = Guid.NewGuid(), StartedAt = older.StartedAt.AddHours(1), Summary = "newer", Outcome = ChangeOutcome.Cancelled };

        history.Save(older);
        history.Save(newer);

        Assert.Equal(["newer", "older"], history.All().Select(r => r.Summary));
        var read = history.Get(older.Id)!;
        Assert.Equal(PathValueKind.String, read.Changes.Values[0].Before.Kind);
        Assert.Equal(@"C:\B", read.Changes.Values[0].After.Value);
        Assert.True(read.CanUndo);
        Assert.StartsWith(store.Root, store.Paths.HistoryDirectory);
    }

    [Fact]
    public void No_history_yet_is_empty()
    {
        using var store = new TempStore();
        Assert.Empty(new FileChangeHistory(store.Paths).All());
    }
}
