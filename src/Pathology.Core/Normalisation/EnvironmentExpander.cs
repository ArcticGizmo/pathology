using System.Text;

namespace Pathology.Core.Normalisation;

/// <summary>The outcome of expanding one string.</summary>
/// <param name="Text">The expanded text. Undefined references stay literal, as Windows leaves them.</param>
/// <param name="Referenced">Every <c>%NAME%</c> referenced, in order of first use.</param>
/// <param name="Unresolved">The references that stayed literal.</param>
public sealed record Expansion(string Text, IReadOnlyList<string> Referenced, IReadOnlyList<string> Unresolved);

/// <summary>
/// <c>%VAR%</c> expansion with <c>ExpandEnvironmentStrings</c> semantics: a single pass (an expanded value
/// isn't re-expanded), case-insensitive names, and an undefined reference left as written.
/// </summary>
public static class EnvironmentExpander
{
    /// <param name="lookup">A variable's value, or null when it isn't defined.</param>
    public static Expansion Expand(string text, Func<string, string?> lookup)
    {
        var output = new StringBuilder(text.Length);
        var referenced = new List<string>();
        var unresolved = new List<string>();

        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('%', i);
            if (open < 0) { output.Append(text, i, text.Length - i); break; }
            output.Append(text, i, open - i);

            var close = text.IndexOf('%', open + 1);
            if (close < 0) { output.Append(text, open, text.Length - open); break; }

            var name = text[(open + 1)..close];
            if (name.Length == 0)
            {
                // "%%": the first % is literal, and the second may open a reference.
                output.Append('%');
                i = close;
                continue;
            }

            // After an undefined reference the scan restarts at its closing %, so the text between two
            // references ("%A%\bin\%B%" → "\bin\") gets looked up too. It can't be a variable, so skip it.
            var plausible = IsPlausibleName(name);
            if (plausible) AddOnce(referenced, name);
            if (plausible && lookup(name) is { } value)
            {
                output.Append(value);
                i = close + 1;
            }
            else
            {
                // Windows copies "%NAME" and carries on from the closing %, which may open the next reference.
                if (plausible) AddOnce(unresolved, name);
                output.Append('%').Append(name);
                i = close;
            }
        }

        return new Expansion(output.ToString(), referenced, unresolved);
    }

    /// <summary>The names a string references, without expanding anything (a <c>REG_SZ</c> value's view).</summary>
    public static IReadOnlyList<string> References(string text) => Expand(text, _ => null).Referenced;

    static bool IsPlausibleName(string name) => name.IndexOfAny(['\\', '/', ';', '"']) < 0;

    static void AddOnce(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }
}
