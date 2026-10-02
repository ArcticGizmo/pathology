using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Tests.Normalisation;

public class PathTokeniserTests
{
    [Fact]
    public void Splits_on_every_separator_and_keeps_empty_slots()
    {
        var segments = PathTokeniser.Split(@"C:\a;;C:\b;");

        Assert.Equal([@"C:\a", "", @"C:\b", ""], segments.Select(s => s.Text));
        Assert.Equal([0, 1, 2, 3], segments.Select(s => s.Index));
        Assert.Equal(HygieneDefects.Empty, segments[1].Defects);
        Assert.Equal(HygieneDefects.Empty | HygieneDefects.TrailingSeparator, segments[3].Defects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_or_empty_value_has_no_entries(string? value) => Assert.Empty(PathTokeniser.Split(value));

    [Theory]
    [InlineData(@"C:\Tools", HygieneDefects.None)]
    [InlineData(@"C:\", HygieneDefects.None)]
    [InlineData(@"\\server\share", HygieneDefects.None)]
    [InlineData(@" C:\Tools", HygieneDefects.LeadingWhitespace)]
    [InlineData("C:\\Tools\t", HygieneDefects.TrailingWhitespace)]
    [InlineData("\"C:\\Program Files\\x\"", HygieneDefects.Quotes)]
    [InlineData(@"C:\Tools\\bin", HygieneDefects.DoubledBackslash)]
    [InlineData(@"\\\server\share", HygieneDefects.DoubledBackslash)]
    [InlineData(@"C:\Tools\", HygieneDefects.TrailingBackslash)]
    [InlineData("C:/Tools", HygieneDefects.ForwardSlash)]
    [InlineData("   ", HygieneDefects.Empty)]
    public void Inspect_finds_each_defect(string text, HygieneDefects expected) =>
        Assert.Equal(expected, PathTokeniser.Inspect(text));

    [Fact]
    public void Defects_combine()
    {
        var defects = PathTokeniser.Inspect(" \"C:\\Tools\\\\bin\\\" ");

        Assert.Equal(
            HygieneDefects.LeadingWhitespace | HygieneDefects.TrailingWhitespace | HygieneDefects.Quotes
            | HygieneDefects.DoubledBackslash | HygieneDefects.TrailingBackslash,
            defects);
    }
}
