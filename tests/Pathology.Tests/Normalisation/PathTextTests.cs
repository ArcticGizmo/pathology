using Pathology.Core.Model;
using Pathology.Core.Normalisation;

namespace Pathology.Tests.Normalisation;

public class PathTextTests
{
    [Theory]
    [InlineData("", PathForm.Empty)]
    [InlineData(@"C:\Tools", PathForm.Absolute)]
    [InlineData(@"c:\", PathForm.Absolute)]
    [InlineData(@"\\?\C:\Tools", PathForm.Absolute)]
    [InlineData(@"C:Tools", PathForm.DriveRelative)]
    [InlineData(@"C:", PathForm.DriveRelative)]
    [InlineData(@"\Tools", PathForm.RootRelative)]
    [InlineData(@"Tools", PathForm.Relative)]
    [InlineData(@".", PathForm.Relative)]
    [InlineData(@"..\bin", PathForm.Relative)]
    [InlineData(@"%JAVA_HOME%\bin", PathForm.Relative)]
    [InlineData(@"\\server\share\bin", PathForm.Unc)]
    [InlineData(@"\\?\UNC\server\share", PathForm.Unc)]
    [InlineData(@"\\?\Volume{0b1a4b6c-1111-2222-3333-444455556666}\bin", PathForm.DevicePath)]
    public void Classify_decides_from_the_text_alone(string text, PathForm expected) =>
        Assert.Equal(expected, PathText.Classify(text));

    [Theory]
    [InlineData(" \"C:/Tools/bin\" ", @"C:\Tools\bin")]
    [InlineData("\tC:\\x\t", @"C:\x")]
    public void Strip_removes_whitespace_quotes_and_forward_slashes(string text, string expected) =>
        Assert.Equal(expected, PathText.Strip(text));

    [Theory]
    [InlineData(@"C:\Tools\", @"C:\Tools")]
    [InlineData(@"c:\Tools\\bin", @"C:\Tools\bin")]
    [InlineData(@"C:\Tools\.\bin\..\lib", @"C:\Tools\lib")]
    [InlineData(@"C:\..\..\Tools", @"C:\Tools")]
    [InlineData(@"C:\Tools.\bin ", @"C:\Tools\bin")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\\", @"C:\")]
    [InlineData(@"\\?\C:\Tools", @"C:\Tools")]
    [InlineData(@"\\server\share\bin\", @"\\server\share\bin")]
    [InlineData(@"\\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\..\..\x", @"\\server\share\x")]
    [InlineData(@"\\?\UNC\server\share\bin", @"\\server\share\bin")]
    public void Canonical_collapses_to_one_spelling(string stripped, string expected) =>
        Assert.Equal(expected, PathText.Canonical(stripped));

    [Theory]
    [InlineData("")]
    [InlineData("Tools")]
    [InlineData(".")]
    [InlineData(@"C:Tools")]
    [InlineData(@"\Tools")]
    public void Canonical_refuses_what_depends_on_the_current_directory(string stripped) =>
        Assert.Null(PathText.Canonical(stripped));

    [Theory]
    [InlineData(@"C:\Tools\bin", @"C:\Tools")]
    [InlineData(@"C:\Tools", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData(@"\\server\share\bin\x", @"\\server\share\bin")]
    [InlineData(@"\\server\share\bin", @"\\server\share")]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"\\?\Volume{abc}\bin", @"\\?\Volume{abc}")]
    public void Parent_walks_up_and_stops_at_the_root(string canonical, string? expected) =>
        Assert.Equal(expected, PathText.Parent(canonical));

    [Fact]
    public void Keys_match_for_spellings_of_the_same_folder()
    {
        var a = PathText.Key(PathText.Canonical(PathText.Strip(@"c:\tools\BIN\"))!);
        var b = PathText.Key(PathText.Canonical(PathText.Strip("\"C:/Tools/bin\""))!);
        Assert.Equal(a, b);
        Assert.Equal(@"C:\", PathText.Key(@"C:\"));
    }
}
