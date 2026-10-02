using Pathology.Core.Normalisation;

namespace Pathology.Tests.Normalisation;

public class EnvironmentExpanderTests
{
    static readonly Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["JAVA_HOME"] = @"C:\Java",
        ["NESTED"] = @"%SystemRoot%\x",
    };

    static Expansion Expand(string text) => EnvironmentExpander.Expand(text, n => Vars.GetValueOrDefault(n));

    [Fact]
    public void Expands_known_variables_case_insensitively()
    {
        var result = Expand(@"%systemroot%\System32");

        Assert.Equal(@"C:\Windows\System32", result.Text);
        Assert.Equal(["systemroot"], result.Referenced);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Leaves_an_undefined_reference_literal_and_reports_it()
    {
        var result = Expand(@"%NOPE%\bin");

        Assert.Equal(@"%NOPE%\bin", result.Text);
        Assert.Equal(["NOPE"], result.Unresolved);
    }

    [Fact]
    public void Is_a_single_pass_like_ExpandEnvironmentStrings()
    {
        // A value that itself contains %VAR% is inserted as-is, not expanded again.
        Assert.Equal(@"%SystemRoot%\x", Expand("%NESTED%").Text);
    }

    [Fact]
    public void An_undefined_name_does_not_swallow_the_next_reference()
    {
        // Windows emits "%NOPE" then treats the closing % as a new opener.
        Assert.Equal(@"%NOPEC:\Java%", Expand("%NOPE%JAVA_HOME%%").Text);
    }

    [Fact]
    public void The_text_between_two_references_is_never_reported_as_a_name()
    {
        var result = Expand(@"%NOPE%\bin\%JAVA_HOME%");

        Assert.Equal(@"%NOPE%\bin\C:\Java", result.Text);
        Assert.Equal(["NOPE", "JAVA_HOME"], result.Referenced);
        Assert.Equal(["NOPE"], result.Unresolved);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("%")]
    [InlineData("%%")]
    [InlineData(@"C:\plain")]
    public void Text_without_a_complete_reference_is_unchanged(string text) => Assert.Equal(text, Expand(text).Text);

    [Fact]
    public void References_lists_names_without_expanding()
    {
        Assert.Equal(["A", "B"], EnvironmentExpander.References(@"%A%\x;%B%\y;%a%"));
    }
}
