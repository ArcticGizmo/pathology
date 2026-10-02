using System.Security.AccessControl;
using System.Security.Principal;
using Pathology.Core.Detection;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Windows;

/// <summary>
/// Designs a folder lock-down from its captured SDDL: what the DACL and owner should become, in words, and as the
/// equivalent <c>icacls</c> commands. Pure: it parses and rebuilds the descriptor in memory and touches nothing.
/// </summary>
/// <remarks>
/// <para>A lock-down keeps every entry's read and execute rights and takes away only the write-class ones (add
/// files or folders, delete, change attributes, permissions or owner), and only from trustees that aren't
/// administrative: Administrators, SYSTEM, TrustedInstaller, service SIDs and CREATOR OWNER keep theirs, as does
/// the one trustee the fix names (you, for your own user PATH folder).</para>
/// <para>Anything it doesn't understand (object or callback entries, a descriptor that won't parse) is refused
/// rather than guessed at.</para>
/// </remarks>
public sealed class AclDesigner : IAclDesigner
{
    // Directory rights. The write class is everything that lets a trustee put or replace something in the folder.
    const int FileAllAccess = 0x1F01FF;
    const int Modify = 0x1301BF;
    const int ReadExecute = 0x1200A9;
    const int Read = 0x120089;
    const int Synchronize = 0x100000;
    const int WriteClass = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000;
    const int GenericAll = 0x10000000, GenericExecute = 0x20000000, GenericWrite = 0x40000000;
    const int GenericRead = unchecked((int)0x80000000);

    static readonly SecurityIdentifier Administrators = new(WellKnownSids.Administrators);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSids.LocalSystem);

    public AclDesign Design(string path, string sddl, AclFix fix, AclDesignMode mode)
    {
        RawSecurityDescriptor descriptor;
        try { descriptor = new RawSecurityDescriptor(sddl); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return new AclDesign { Refusal = "Its permissions couldn't be parsed, so PATHology won't rewrite them." };
        }

        var original = descriptor.DiscretionaryAcl;
        var aces = new List<CommonAce>();
        foreach (var ace in original?.Cast<GenericAce>() ?? [])
        {
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed or AceQualifier.AccessDenied } common)
                return new AclDesign { Refusal = "Its permissions include unusual entries (object or conditional ones), so PATHology won't rewrite them. Change them by hand." };
            aces.Add(Copy(common));
        }

        var lines = new List<string>();
        var commands = new List<string>();
        var quoted = $"\"{path}\"";
        var owner = descriptor.Owner;
        var newOwner = fix.ResetOwner ? Administrators : owner;
        var protect = mode == AclDesignMode.Protect && fix.StripWrite;

        if (original is null)
        {
            if (!protect)
                return new AclDesign { Refusal = "It has no permissions at all (so everyone has full control), and only a full lock-down can fix that." };
            lines.Add("Everyone: full control (it had no permissions set) → nothing");
        }

        if (protect)
        {
            if (!descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
            {
                lines.Add("Inheritance: on → off (what was inherited is kept as the folder's own, then trimmed)");
                commands.Add($"icacls {quoted} /inheritance:d");
            }
            aces = aces.Select(a => a.IsInherited ? WithFlags(a, a.AceFlags & ~AceFlags.Inherited) : a).ToList();
        }

        if (fix.StripWrite)
        {
            // Inherited entries are trimmed too when inheritance stays on: that's what they'll be once the folder above
            // is locked down, and the projection evaluates this descriptor. Only the folder's own entries are written.
            var trimmed = new List<CommonAce>();
            var changed = new Dictionary<string, (int Before, int After, bool Own)>(StringComparer.OrdinalIgnoreCase);
            foreach (var ace in aces)
            {
                var sid = ace.SecurityIdentifier.Value;
                var mapped = Map(ace.AccessMask);
                if (ace.AceQualifier != AceQualifier.AccessAllowed || (mapped & WriteClass) == 0 || !Strippable(sid, fix, newOwner))
                {
                    trimmed.Add(ace);
                    continue;
                }

                var kept = mapped & ~WriteClass;
                var (before, after, own) = changed.GetValueOrDefault(sid);
                changed[sid] = (before | mapped, after | ((kept & ~Synchronize) == 0 ? 0 : kept), own || !ace.IsInherited);
                if ((kept & ~Synchronize) != 0) trimmed.Add(WithMask(ace, kept));
            }
            aces = trimmed;

            foreach (var (sid, (before, after, own)) in changed)
            {
                lines.Add($"{Name(sid, fix)}: {Rights(before)} → {(after == 0 ? "nothing" : Rights(after))}" + (own ? "" : " (through inheritance)"));
                if (own) commands.AddRange(GrantCommands(quoted, sid, aces.Where(a => a.SecurityIdentifier.Value == sid && !a.IsInherited)));
            }

            if (protect)
            {
                foreach (var (sid, name) in new[] { (LocalSystem, "SYSTEM"), (Administrators, "Administrators") })
                {
                    if (aces.Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier == sid && FullControl(a))) continue;
                    aces.Add(new CommonAce(AceFlags.ObjectInherit | AceFlags.ContainerInherit, AceQualifier.AccessAllowed, FileAllAccess, sid, false, null));
                    lines.Add($"{name}: full control (added, so administrators can always manage it)");
                    commands.Add($"icacls {quoted} /grant *{sid.Value}:(OI)(CI)F");
                }

                if (fix.KeepWriteSid is { } keep && !aces.Any(a => a.AceQualifier == AceQualifier.AccessAllowed
                        && a.SecurityIdentifier.Value == keep && (Map(a.AccessMask) & 0x2) != 0 && !a.AceFlags.HasFlag(AceFlags.InheritOnly)))
                {
                    aces.Add(new CommonAce(AceFlags.ObjectInherit | AceFlags.ContainerInherit, AceQualifier.AccessAllowed, Modify, new SecurityIdentifier(keep), false, null));
                    lines.Add("You: modify (added, so you keep the access you had)");
                    commands.Add($"icacls {quoted} /grant *{keep}:(OI)(CI)M");
                }
            }
        }

        if (fix.ResetOwner && owner != Administrators)
        {
            lines.Add($"Owner: {(owner is null ? "nobody" : Name(owner.Value, fix))} → Administrators");
            commands.Add($"icacls {quoted} /setowner *{WellKnownSids.Administrators}");
        }

        var flags = descriptor.ControlFlags | ControlFlags.DiscretionaryAclPresent;
        if (protect) flags |= ControlFlags.DiscretionaryAclProtected;
        var dacl = new RawAcl(GenericAcl.AclRevision, aces.Count);
        foreach (var ace in Canonical(aces)) dacl.InsertAce(dacl.Count, ace);
        var designed = new RawSecurityDescriptor(flags, newOwner, descriptor.Group, descriptor.SystemAcl, dacl);

        return new AclDesign
        {
            Sddl = designed.GetSddlForm(AccessControlSections.All),
            OwnerSid = newOwner?.Value,
            Lines = lines,
            Commands = commands,
        };
    }

    /// <summary>
    /// Whether a trustee's write access goes: everyone's but the administrative trustees', CREATOR OWNER's (it stands
    /// for whoever creates a subfolder, and only those who can create one get it) and the one the fix keeps. OWNER
    /// RIGHTS stands for the owner, so it goes when the owner (after any reset) isn't an administrator.
    /// </summary>
    static bool Strippable(string sid, AclFix fix, SecurityIdentifier? owner)
    {
        if (string.Equals(sid, fix.KeepWriteSid, StringComparison.OrdinalIgnoreCase)) return false;
        if (sid == WellKnownSids.CreatorOwner) return false;
        if (sid == WellKnownSids.OwnerRights) return !Writability.IsAdministrativeOwner(owner?.Value);
        return !Writability.IsAdministrativeOwner(sid);
    }

    /// <summary>Explicit deny, explicit allow, inherited deny, inherited allow: the order Windows requires.</summary>
    static IEnumerable<CommonAce> Canonical(List<CommonAce> aces) => aces
        .Select((a, i) => (a, i))
        .OrderBy(p => (p.a.IsInherited ? 2 : 0) + (p.a.AceQualifier == AceQualifier.AccessDenied ? 0 : 1))
        .ThenBy(p => p.i)
        .Select(p => p.a);

    /// <summary>One <c>icacls</c> grant per remaining entry for a trustee (the first replacing its old grants), or a removal.</summary>
    static IEnumerable<string> GrantCommands(string quoted, string sid, IEnumerable<CommonAce> remaining)
    {
        var list = remaining.Where(a => a.AceQualifier == AceQualifier.AccessAllowed).ToList();
        if (list.Count == 0)
        {
            yield return $"icacls {quoted} /remove:g *{sid}";
            yield break;
        }
        for (var i = 0; i < list.Count; i++)
            yield return $"icacls {quoted} /grant{(i == 0 ? ":r" : "")} *{sid}:{Inheritance(list[i].AceFlags)}{IcaclsRights(list[i].AccessMask)}";
    }

    static string Inheritance(AceFlags flags) =>
        (flags.HasFlag(AceFlags.ObjectInherit) ? "(OI)" : "") + (flags.HasFlag(AceFlags.ContainerInherit) ? "(CI)" : "")
        + (flags.HasFlag(AceFlags.InheritOnly) ? "(IO)" : "") + (flags.HasFlag(AceFlags.NoPropagateInherit) ? "(NP)" : "");

    static string IcaclsRights(int mask)
    {
        var m = Map(mask);
        if ((m & FileAllAccess) == FileAllAccess) return "F";
        if ((m & ~Synchronize) == (Modify & ~Synchronize)) return "M";
        if ((m & ~Synchronize) == (ReadExecute & ~Synchronize)) return "RX";
        if ((m & ~Synchronize) == (Read & ~Synchronize)) return "R";

        // Generic rights stay generic on an inherit-only entry, so they're named as such.
        var names = new List<string>();
        if ((mask & GenericRead) != 0) names.Add("GR");
        if ((mask & GenericExecute) != 0) names.Add("GE");
        foreach (var (bit, name) in SpecificNames)
            if ((mask & bit) != 0) names.Add(name);
        return "(" + string.Join(",", names) + ")";
    }

    static readonly (int Bit, string Name)[] SpecificNames =
    [
        (0x1, "RD"), (0x2, "WD"), (0x4, "AD"), (0x8, "REA"), (0x10, "WEA"), (0x20, "X"), (0x40, "DC"), (0x80, "RA"),
        (0x100, "WA"), (0x10000, "DE"), (0x20000, "RC"), (0x40000, "WDAC"), (0x80000, "WO"), (0x100000, "S"),
    ];

    /// <summary>A mask in words: "full control", "modify", "read &amp; execute", or its write-class rights.</summary>
    static string Rights(int mask)
    {
        var m = Map(mask);
        if ((m & FileAllAccess) == FileAllAccess) return "full control";
        if ((m & Modify) == Modify) return "modify";
        if ((m & WriteClass) == 0) return (m & ReadExecute) == ReadExecute ? "read & execute" : (m & Read) == Read ? "read" : "list";
        return Writability.Rights((FileAccessRights)(uint)m);
    }

    static string Name(string sid, AclFix fix) =>
        string.Equals(sid, fix.KeepWriteSid, StringComparison.OrdinalIgnoreCase) ? "You" : WellKnownSids.NameOf(sid);

    static bool FullControl(CommonAce ace) =>
        (Map(ace.AccessMask) & FileAllAccess) == FileAllAccess && !ace.AceFlags.HasFlag(AceFlags.InheritOnly);

    /// <summary>Generic bits folded into the directory rights they stand for.</summary>
    static int Map(int mask)
    {
        var m = mask & 0x00FFFFFF;
        if ((mask & GenericAll) != 0) m |= FileAllAccess;
        if ((mask & GenericRead) != 0) m |= Read;
        if ((mask & GenericWrite) != 0) m |= 0x120116;
        if ((mask & GenericExecute) != 0) m |= 0x1200A0;
        return m;
    }

    static CommonAce Copy(CommonAce a) => new(a.AceFlags, a.AceQualifier, a.AccessMask, a.SecurityIdentifier, false, null);
    static CommonAce WithFlags(CommonAce a, AceFlags flags) => new(flags, a.AceQualifier, a.AccessMask, a.SecurityIdentifier, false, null);
    static CommonAce WithMask(CommonAce a, int mask) => new(a.AceFlags, a.AceQualifier, mask, a.SecurityIdentifier, false, null);
}
