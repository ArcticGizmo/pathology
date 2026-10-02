namespace Pathology.Windows.Tests;

/// <summary>
/// Assertions that never print the values compared. These tests run on a real machine, so their paths and
/// SIDs carry a username, a profile folder or an account ID, and a failure message would copy them into CI
/// logs and terminals. Compare quietly and name only what differed.
/// </summary>
internal static class Quiet
{
    public static void Same(string? expected, string? actual, string what) =>
        Assert.True(
            string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
            $"{what} differs (values withheld: they identify this machine)");
}
