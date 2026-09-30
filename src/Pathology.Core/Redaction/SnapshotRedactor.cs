using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pathology.Core.Model;

namespace Pathology.Core.Redaction;

/// <summary>
/// Scrubs a snapshot of anything that identifies a person or a machine, so it can become a test fixture or a
/// bug report. It must run before any snapshot is written (<see cref="PathSnapshotJson.Write"/> refuses one it
/// hasn't seen).
/// </summary>
/// <remarks>
/// <para>Every string in the snapshot is rewritten, whatever field it's in, so a field added later is covered
/// without anyone remembering to add it here:</para>
/// <list type="bullet">
/// <item>Account SIDs keep their shape and RID but lose their domain: each distinct <c>S-1-5-21-a-b-c</c> becomes
/// <c>S-1-5-21-0-0-n</c>, consistently, so "owned by the current user" still holds after redaction. Azure AD
/// SIDs (<c>S-1-12-1-…</c>, which encode the account's object ID) are renumbered the same way.</item>
/// <item>Profile folders become <c>&lt;user&gt;</c>: the captured profile path, any <c>\Users\name</c> segment
/// (8.3 short forms and other people's profiles included), and the username as a whole word.</item>
/// <item>The machine name and user domain become <c>&lt;machine&gt;</c> and <c>&lt;domain&gt;</c>; UNC hosts
/// become <c>&lt;host&gt;</c>; <c>OneDrive - Org</c> becomes <c>OneDrive - &lt;org&gt;</c>; email addresses go.</item>
/// </list>
/// The placeholders contain <c>&lt;</c> and <c>&gt;</c>, which no real path can, so a redacted value is never
/// mistaken for a live one. Redacting twice changes nothing.
/// </remarks>
public static class SnapshotRedactor
{
    public const string UserPlaceholder = "<user>";
    public const string MachinePlaceholder = "<machine>";
    public const string DomainPlaceholder = "<domain>";
    public const string HostPlaceholder = "<host>";

    /// <summary>Domains that name a Windows authority rather than an organisation, so they stay.</summary>
    static readonly HashSet<string> NeutralDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "NT AUTHORITY", "BUILTIN", "WORKGROUP", "AzureAD", "NT SERVICE", "Window Manager", "Font Driver Host",
    };

    /// <summary>Profile folders that belong to Windows, not a person.</summary>
    const string SharedProfiles = @"Public|Default|Default User|All Users|defaultuser0|<user>";

    // No trailing \b: in SDDL the owner SID runs straight into the next section ("O:S-1-5-21-…-1001G:SY").
    static readonly Regex DomainSid = new(@"\bS-1-5-21-(?!0-0-)(\d+-\d+-\d+)(?!\d)", RegexOptions.CultureInvariant);
    static readonly Regex AzureSid = new(@"\bS-1-12-1-(?!0-0-0-)(\d+-\d+-\d+-\d+)(?!\d)", RegexOptions.CultureInvariant);

    static readonly Regex UsersSegment = new(
        $@"(?<=\\Users\\)(?!(?:{SharedProfiles})(?:[\\;""]|$))[^\\;""]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // \\host\share, and the device forms \\?\UNC\host\share and \\.\UNC\host\share.
    static readonly Regex UncHost = new(
        @"(?<=(?:^|[;""\s=])\\\\(?:[?.]\\UNC\\)?)(?![?.]\\|<host>)[^\\;""\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex OneDriveOrg = new(@"(?<=OneDrive - )(?!<org>)[^\\;""]+", RegexOptions.CultureInvariant);

    static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The redaction pass writes enums as numbers, so only free text is scrubbed: an account named "User" or
    /// "System" mustn't rewrite <c>PathScope.User</c> or <c>Perspective.System</c>.
    /// </summary>
    static readonly JsonSerializerOptions WorkingOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A redacted copy of the snapshot.</summary>
    public static PathSnapshot Redact(PathSnapshot snapshot)
    {
        var scrubber = new Scrubber(snapshot.Host);
        var node = JsonSerializer.SerializeToNode(snapshot, WorkingOptions)!;
        scrubber.Walk(node);

        var redacted = node.Deserialize<PathSnapshot>(WorkingOptions)!;
        return redacted with
        {
            Redacted = true,
            // A placeholder can change a key's case ("C:\USERS\ALEX" → "C:\Users\<user>"). Keys are compare-only and
            // upper-cased by definition, so they're restored to that.
            Entries = redacted.Entries.Select(e => e with { Key = e.Key.ToUpperInvariant() }).ToList(),
            Host = redacted.Host with
            {
                MachineName = MachinePlaceholder,
                UserName = UserPlaceholder,
                UserDomain = NeutralDomains.Contains(snapshot.Host.UserDomain) ? snapshot.Host.UserDomain : DomainPlaceholder,
            },
        };
    }

    /// <summary>
    /// Which identifying values of <paramref name="host"/> still appear in <paramref name="json"/>, by
    /// <b>category</b> only ("user SID", never the SID), so the answer is safe to print. Empty means clean.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(string json, HostInfo host)
    {
        // Every string value (and property name, since dictionary keys are data too), unescaped.
        var strings = new List<string>();
        Collect(JsonNode.Parse(json), strings);
        var text = string.Join('\n', strings);

        var leaks = new List<string>();
        void Substring(string category, string value, int minLength)
        {
            if (value.Length >= minLength && text.Contains(value, StringComparison.OrdinalIgnoreCase)) leaks.Add(category);
        }
        void Word(string category, string value)
        {
            if (WordPattern(value) is { } pattern && pattern.IsMatch(text)) leaks.Add(category);
        }

        Substring("user profile path", host.UserProfile, 4);
        Substring("user SID", host.UserSid, 9);
        if (DomainSid.Match(host.UserSid) is { Success: true } m) Substring("domain SID", "S-1-5-21-" + m.Groups[1].Value, 9);
        Word("machine name", host.MachineName);
        if (!NeutralDomains.Contains(host.UserDomain)) Word("user domain", host.UserDomain);
        Word("username", host.UserName);
        if (host.UserName.Length > 0
            && Regex.IsMatch(text, $@"\\{Regex.Escape(host.UserName)}(?=[\\;""]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            leaks.Add("username in a path");
        return leaks.Distinct().ToList();
    }

    static void Collect(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj) { into.Add(key); Collect(child, into); }
                break;
            case JsonArray array:
                foreach (var child in array) Collect(child, into);
                break;
            case JsonValue value when value.TryGetValue<string>(out var s):
                into.Add(s);
                break;
        }
    }

    /// <summary>A whole-word matcher, or null for a name too short to replace without mangling other text.</summary>
    static Regex? WordPattern(string name) => name.Length < 3
        ? null
        : new Regex($@"(?<![A-Za-z0-9_.-]){Regex.Escape(name)}(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    sealed class Scrubber(HostInfo host)
    {
        readonly Dictionary<string, int> _domains = [];
        readonly Dictionary<string, int> _azure = [];

        readonly Regex? _userWord = WordPattern(host.UserName);
        readonly Regex? _machineWord = WordPattern(host.MachineName);
        readonly Regex? _domainWord = NeutralDomains.Contains(host.UserDomain)
            || string.Equals(host.UserDomain, host.MachineName, StringComparison.OrdinalIgnoreCase)
            ? null : WordPattern(host.UserDomain);

        /// <summary>Scrub every string in the tree, in place.</summary>
        public void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                        if (ScrubbedLeaf(obj[key]) is { } leaf) obj[key] = leaf; else Walk(obj[key]);
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        if (ScrubbedLeaf(array[i]) is { } leaf) array[i] = leaf; else Walk(array[i]);
                    break;
            }
        }

        JsonValue? ScrubbedLeaf(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var s) ? JsonValue.Create(Scrub(s)) : null;

        public string Scrub(string s)
        {
            s = DomainSid.Replace(s, m => "S-1-5-21-0-0-" + Number(_domains, m.Groups[1].Value));
            s = AzureSid.Replace(s, m => "S-1-12-1-0-0-0-" + Number(_azure, m.Groups[1].Value));

            if (host.UserProfile.Length > 3)
            {
                var parent = host.UserProfile.TrimEnd('\\');
                var cut = parent.LastIndexOf('\\');
                if (cut > 0)
                    s = Regex.Replace(s, Regex.Escape(parent) + @"(?=[\\;""]|$)", parent[..cut] + @"\" + UserPlaceholder, RegexOptions.IgnoreCase);
            }
            s = UsersSegment.Replace(s, UserPlaceholder);
            if (host.UserName.Length > 0)
                s = Regex.Replace(s, $@"(?<=\\){Regex.Escape(host.UserName)}(?=[\\;""]|$)", UserPlaceholder, RegexOptions.IgnoreCase);

            s = UncHost.Replace(s, HostPlaceholder);
            s = OneDriveOrg.Replace(s, "<org>");
            s = Email.Replace(s, "<email>");

            if (_userWord is not null) s = _userWord.Replace(s, UserPlaceholder);
            if (_machineWord is not null) s = _machineWord.Replace(s, MachinePlaceholder);
            if (_domainWord is not null) s = _domainWord.Replace(s, DomainPlaceholder);
            return s;
        }

        static int Number(Dictionary<string, int> seen, string key)
        {
            if (!seen.TryGetValue(key, out var n)) seen[key] = n = seen.Count + 1;
            return n;
        }
    }
}
