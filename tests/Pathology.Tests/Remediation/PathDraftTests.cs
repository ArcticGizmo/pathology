using Pathology.Core.Model;
using Pathology.Core.Remediation;
using Pathology.Tests.Detection;

namespace Pathology.Tests.Remediation;

public class PathDraftTests
{
    static PathDraft Draft(string machine, string? user = null, PathValueKind userKind = PathValueKind.ExpandString) =>
        PathDraft.From(new TestMachine(machine, user, userKind: userKind).Snapshot());

    [Fact]
    public void An_unedited_draft_writes_back_exactly_what_was_stored()
    {
        var snapshot = new TestMachine(@"%SystemRoot%\system32;;C:\Tools\;", @"C:\Users\you\bin;""C:\x"" ").Snapshot();
        var draft = PathDraft.From(snapshot);

        Assert.Equal(snapshot.MachinePath.Value, draft.ValueOf(PathScope.Machine));
        Assert.Equal(snapshot.UserPath.Value, draft.ValueOf(PathScope.User));
        Assert.True(ChangeSet.From(snapshot, draft).IsEmpty);
    }

    [Fact]
    public void The_trailing_separator_is_not_an_entry_but_is_kept()
    {
        var draft = Draft(@"C:\A;C:\B;");

        Assert.Equal([@"C:\A", @"C:\B"], draft.Machine.Select(e => e.Text));
        Assert.Equal(@"C:\Z;C:\B;", draft.Apply(new ReplaceText(0, @"C:\Z")).ValueOf(PathScope.Machine));
        Assert.Equal(@"C:\B;", draft.Apply(new RemoveEntry(0)).ValueOf(PathScope.Machine));
    }

    [Fact]
    public void Ids_name_entries_across_scopes_in_search_order()
    {
        var draft = Draft(@"C:\A;C:\B", @"C:\U");

        Assert.Equal([0, 1], draft.Machine.Select(e => e.Id));
        Assert.Equal([2], draft.User.Select(e => e.Id));
        Assert.Equal(new EntryOrigin(PathScope.User, 0), draft.User[0].Origin);
    }

    [Fact]
    public void Moving_to_the_other_scope_keeps_the_entry_and_its_id()
    {
        var draft = Draft(@"C:\A;C:\B", @"C:\U").Apply(new MoveEntry(1, PathScope.User, 0));

        Assert.Equal(@"C:\A", draft.ValueOf(PathScope.Machine));
        Assert.Equal(@"C:\B;C:\U", draft.ValueOf(PathScope.User));
        Assert.Equal((PathScope.User, 0), draft.Locate(1));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void Entries_moved_to_the_user_path_keep_their_machine_order_whichever_goes_first(int first, int second)
    {
        var draft = Draft(@"C:\A;C:\B;C:\C", @"C:\U").Apply([new MoveToUser(first), new MoveToUser(second)]);

        Assert.Equal(@"C:\A", draft.ValueOf(PathScope.Machine));
        Assert.Equal(@"C:\B;C:\C;C:\U", draft.ValueOf(PathScope.User));
    }

    [Fact]
    public void MoveToUser_leaves_a_user_entry_where_it_is()
    {
        var draft = Draft(@"C:\A", @"C:\U;C:\V").Apply(new MoveToUser(2));

        Assert.Equal(@"C:\U;C:\V", draft.ValueOf(PathScope.User));
    }

    [Fact]
    public void Moving_within_a_scope_counts_the_index_once_the_entry_is_out()
    {
        var draft = Draft(@"C:\A;C:\B;C:\C").Apply(new MoveEntry(0, PathScope.Machine, 2));

        Assert.Equal(@"C:\B;C:\C;C:\A", draft.ValueOf(PathScope.Machine));
    }

    [Fact]
    public void An_edit_naming_an_entry_that_is_gone_does_nothing()
    {
        var draft = Draft(@"C:\A;C:\B").Apply([new RemoveEntry(0), new ReplaceText(0, @"C:\X"), new MoveEntry(0, PathScope.User, 0)]);

        Assert.Equal(@"C:\B", draft.ValueOf(PathScope.Machine));
        Assert.Empty(draft.User);
    }

    [Fact]
    public void Adding_to_a_missing_value_creates_it_as_REG_EXPAND_SZ()
    {
        var draft = Draft(@"C:\A");
        Assert.Equal(PathValueKind.Missing, draft.UserKind);
        Assert.Null(draft.ValueOf(PathScope.User));

        draft = draft.Apply(new AddEntry(PathDraft.ManualIdBase, PathScope.User, 5, @"C:\Users\you\bin"));

        Assert.Equal(PathValueKind.ExpandString, draft.UserKind);
        Assert.Equal(@"C:\Users\you\bin", draft.ValueOf(PathScope.User));
    }

    [Fact]
    public void Adding_the_same_id_twice_adds_it_once()
    {
        var add = new AddEntry(PathDraft.ManualIdBase, PathScope.Machine, 0, @"C:\New");
        var draft = Draft(@"C:\A").Apply([add, add]);

        Assert.Equal(@"C:\New;C:\A", draft.ValueOf(PathScope.Machine));
    }

    [Fact]
    public void Removing_every_entry_leaves_an_empty_value_not_a_missing_one()
    {
        var draft = Draft(@"C:\A").Apply(new RemoveEntry(0));

        Assert.Equal("", draft.ValueOf(PathScope.Machine));
        Assert.Equal(PathValueKind.ExpandString, draft.RawOf(PathScope.Machine).Kind);
    }

    [Fact]
    public void Reorder_puts_the_named_entries_first_and_keeps_the_rest_in_order()
    {
        var draft = Draft(@"C:\A;C:\B;C:\C;C:\D").Apply(new Reorder(PathScope.Machine, [2, 99, 1]));

        Assert.Equal(@"C:\C;C:\B;C:\A;C:\D", draft.ValueOf(PathScope.Machine));
    }

    [Fact]
    public void SetKind_changes_only_the_kind()
    {
        var draft = Draft(@"C:\A", @"%USERPROFILE%\bin", PathValueKind.String).Apply(new SetKind(PathScope.User, PathValueKind.ExpandString));

        Assert.Equal(PathValueKind.ExpandString, draft.UserKind);
        Assert.Equal(@"%USERPROFILE%\bin", draft.ValueOf(PathScope.User));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\x\"", HygieneDefects.Quotes, @"C:\Program Files\x")]
    [InlineData("\" C:\\x \"", HygieneDefects.Quotes, @"C:\x")]
    [InlineData("  C:\\x ", HygieneDefects.LeadingWhitespace | HygieneDefects.TrailingWhitespace, @"C:\x")]
    [InlineData(@"C:\Tools\\bin\\\x", HygieneDefects.DoubledBackslash, @"C:\Tools\bin\x")]
    [InlineData(@"\\server\share\\x", HygieneDefects.DoubledBackslash, @"\\server\share\x")]
    [InlineData("C:/Tools/bin", HygieneDefects.ForwardSlash, @"C:\Tools\bin")]
    [InlineData(@"C:\Tools\", EntryText.Tidyable, @"C:\Tools\")]
    public void Tidy_cleans_only_what_it_is_asked_to(string text, HygieneDefects defects, string expected) =>
        Assert.Equal(expected, EntryText.Tidy(text, defects));

    [Fact]
    public void Tidy_leaves_other_defects_alone()
    {
        Assert.Equal(" C:/x", EntryText.Tidy(" C:/x ", HygieneDefects.TrailingWhitespace));
    }

    [Fact]
    public void TidyText_applies_to_the_text_as_it_is_by_then()
    {
        var draft = Draft("\"C:/Tools\"")
            .Apply([new TidyText(0, HygieneDefects.Quotes), new TidyText(0, HygieneDefects.ForwardSlash)]);

        Assert.Equal(@"C:\Tools", draft.ValueOf(PathScope.Machine));
    }
}
