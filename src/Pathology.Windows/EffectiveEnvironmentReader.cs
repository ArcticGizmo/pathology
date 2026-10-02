using System.Collections;
using System.ComponentModel;
using Pathology.Core.Capture;
using Pathology.Windows.Native;
using static Pathology.Windows.Native.NativeMethods;

namespace Pathology.Windows;

/// <summary>
/// The environment a process started now would receive, built by <c>CreateEnvironmentBlock</c> from the
/// registry with <c>bInherit: false</c> (so nothing leaks in from this process), and this process's own.
/// </summary>
public sealed unsafe class EffectiveEnvironmentReader : IEffectiveEnvironmentReader
{
    public IReadOnlyDictionary<string, string> ReadNewProcessEnvironment()
    {
        using var token = TokenReader.OpenCurrent(TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE);
        if (!CreateEnvironmentBlock(out var block, token, inherit: false)) throw new Win32Exception();
        try { return Parse((char*)block); }
        finally { DestroyEnvironmentBlock(block); }
    }

    public IReadOnlyDictionary<string, string> ReadCurrentProcessEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            values[(string)e.Key] = (string?)e.Value ?? "";
        return values;
    }

    /// <summary>A block of <c>NAME=value\0</c> strings ending in an empty one.</summary>
    internal static Dictionary<string, string> Parse(char* block)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var p = block; *p != '\0';)
        {
            var entry = new string(p);
            p += entry.Length + 1;
            // Skip the hidden per-drive current-directory entries ("=C:=C:\...").
            var eq = entry.IndexOf('=', 1);
            if (entry[0] == '=' || eq < 0) continue;
            values[entry[..eq]] = entry[(eq + 1)..];
        }
        return values;
    }
}
