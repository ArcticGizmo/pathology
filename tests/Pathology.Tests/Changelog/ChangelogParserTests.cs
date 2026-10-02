using Pathology.Core.Changelog;

namespace Pathology.Tests.Changelog;

public class ChangelogParserTests
{
    const string Sample = """
        # Changelog

        ---

        ## [Unreleased]

        ---

        ## [v0.2.0] - 2026-10-07

        ### Added
        - Second thing.

        ---

        ## [v0.1.0] - 2026-09-30

        - First thing.

        ---
        """;

    [Fact]
    public void Parse_finds_every_section_newest_first()
    {
        var sections = ChangelogParser.Parse(Sample);

        Assert.Equal(3, sections.Count);
        Assert.Equal("[Unreleased]", sections[0].Heading);
        Assert.Null(sections[0].Version);
        Assert.Equal(new Version(0, 2, 0), sections[1].Version);
        Assert.Equal("v0.2.0", sections[1].Display);
        Assert.Equal(new Version(0, 1, 0), sections[2].Version);
    }

    [Fact]
    public void Parse_trims_the_trailing_rule_and_blank_lines_from_a_block()
    {
        var section = ChangelogParser.Parse(Sample)[2];

        Assert.Equal("## [v0.1.0] - 2026-09-30", section.Block[0]);
        Assert.Equal("- First thing.", section.Block[^1]);
    }

    [Fact]
    public void UnseenSince_returns_only_versions_above_last_seen()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "0.1.0", "0.2.0");

        Assert.Single(unseen);
        Assert.Equal("v0.2.0", unseen[0].Display);
    }

    [Fact]
    public void UnseenSince_never_includes_the_unreleased_section()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "0.0.1", "9.9.9");

        Assert.Equal(2, unseen.Count);
        Assert.All(unseen, s => Assert.NotNull(s.Version));
    }

    [Fact]
    public void UnseenSince_returns_nothing_on_a_fresh_install()
    {
        // No last-seen version means there is no history to diff against — showing the whole
        // changelog to someone who just installed would be noise, not news.
        Assert.Empty(ChangelogParser.UnseenSince(Sample, null, "0.2.0"));
        Assert.Empty(ChangelogParser.UnseenSince(Sample, "", "0.2.0"));
    }

    [Fact]
    public void UnseenSince_returns_nothing_when_already_current()
        => Assert.Empty(ChangelogParser.UnseenSince(Sample, "0.2.0", "0.2.0"));

    [Fact]
    public void UnseenSince_excludes_versions_above_the_running_one()
    {
        // Defensive: the embedded changelog can describe a version newer than the running build if a
        // release was documented before it shipped. Don't advertise what isn't installed.
        var unseen = ChangelogParser.UnseenSince(Sample, "0.0.1", "0.1.0");

        Assert.Single(unseen);
        Assert.Equal("v0.1.0", unseen[0].Display);
    }

    [Fact]
    public void Parse_of_empty_input_yields_nothing()
        => Assert.Empty(ChangelogParser.Parse(""));

    [Fact]
    public void The_projects_own_changelog_parses()
    {
        // Guards the real file's format: a heading that stops matching would silently disable the
        // whole "what's new" flow.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CHANGELOG.md");
        var markdown = File.ReadAllText(Path.GetFullPath(path));

        var sections = ChangelogParser.Parse(markdown);

        Assert.NotEmpty(sections);
        Assert.Equal("[Unreleased]", sections[0].Heading);
    }
}
