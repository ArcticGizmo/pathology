using Pathology.App.Changelog;

namespace Pathology.Tests.Changelog;

public class ChangelogMarkdownTests
{
    [Fact]
    public void A_wrapped_bullet_is_joined_into_one_line()
    {
        var joined = ChangelogMarkdown.JoinContinuations(
        [
            "- **The shell** — five pages, all empty,",
            "  all honest about it.",
            "- Second bullet.",
        ]).ToList();

        Assert.Equal(["- **The shell** — five pages, all empty, all honest about it.", "- Second bullet."], joined);
    }

    [Fact]
    public void Headings_blank_lines_and_rules_pass_through_untouched()
    {
        string[] lines = ["## [v0.1.0] - 2026-09-30", "", "### Added", "- Thing.", "", "---"];

        Assert.Equal(lines, ChangelogMarkdown.JoinContinuations(lines));
    }

    [Fact]
    public void An_indented_line_that_follows_no_bullet_is_left_alone()
    {
        string[] lines = ["Some prose.", "  indented prose"];

        Assert.Equal(lines, ChangelogMarkdown.JoinContinuations(lines));
    }

    [Fact]
    public void Windows_line_endings_are_trimmed()
        => Assert.Equal(["- One two."], ChangelogMarkdown.JoinContinuations(["- One\r", "  two.\r"]));
}
