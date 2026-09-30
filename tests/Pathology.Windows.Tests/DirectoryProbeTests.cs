using Pathology.Core.Model;
using Xunit.Abstractions;

namespace Pathology.Windows.Tests;

/// <summary>
/// The probe and evaluator together, over temp folders with crafted ACLs. Proves the probe captures a
/// descriptor the evaluator reads correctly, never follows a link, and never goes near the network.
/// </summary>
public sealed class DirectoryProbeTests(ITestOutputHelper output) : IDisposable
{
    const string NetworkTarget = @"\\pathology-test.invalid\share";

    readonly TempTree _tree = new();
    readonly DirectoryProbe _probe = new();
    readonly AccessEvaluator _evaluator = new();

    static string Me => TempTree.CurrentUserSid;

    AccessResult Evaluate(DirectoryFacts facts, PerspectiveIdentity who)
    {
        Assert.NotNull(facts.Sddl);
        return _evaluator.Evaluate(facts.Sddl!, who);
    }

    [Fact]
    public void An_existing_folder_is_read_with_its_owner_and_descriptor()
    {
        var path = _tree.Folder("plain");

        var facts = _probe.Probe(path, allowNetwork: false);

        Assert.Equal(ProbeStatus.Probed, facts.Status);
        Assert.True(facts.Exists);
        Assert.True(facts.IsDirectory);
        Assert.Equal(DriveKind.Fixed, facts.Drive);
        Assert.False(facts.IsReparsePoint);
        Assert.NotNull(facts.OwnerSid);
        Assert.True(facts.Sddl!.StartsWith("O:", StringComparison.Ordinal) && facts.Sddl.Contains("D:"), "the SDDL should carry an owner and a DACL");
        Assert.Null(facts.SecurityError);
    }

    [Fact]
    public void A_missing_folder_is_reported_and_not_created()
    {
        var path = _tree.PathOf(@"missing\deeper");

        var facts = _probe.Probe(path, allowNetwork: false);

        Assert.Equal(ProbeStatus.Probed, facts.Status);
        Assert.False(facts.Exists);
        Assert.False(Directory.Exists(_tree.PathOf("missing")));
    }

    [Fact]
    public void A_folder_Users_can_modify_is_writable_for_a_standard_user()
    {
        var path = _tree.Folder("users-writable");
        _tree.SetAcl(path, $"D:PAI(A;OICI;FA;;;{Me})(A;OICI;FA;;;SY)(A;;0x1301bf;;;BU)");

        var result = Evaluate(_probe.Probe(path, false), TokenPerspectives.StandardUser);

        Assert.True(result.CanAddFiles);
        Assert.Equal(WellKnownSids.Users, Assert.Single(result.GrantedBy).Sid);
    }

    [Fact]
    public void An_owner_only_folder_is_not_writable_for_a_standard_user_but_its_owner_can_rewrite_the_ACL()
    {
        var path = _tree.Folder("owner-only");
        _tree.SetAcl(path, $"O:{Me}D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        var facts = _probe.Probe(path, false);
        var owner = new PerspectiveIdentity
        {
            Perspective = Perspective.CurrentUserUnelevated,
            UserSid = Me,
            Groups = [new(WellKnownSids.Everyone, GroupAttributes.Enabled), new(WellKnownSids.MediumIntegrity, GroupAttributes.Integrity | GroupAttributes.IntegrityEnabled)],
        };

        Quiet.Same(Me, facts.OwnerSid, "the owner");
        Assert.False(Evaluate(facts, TokenPerspectives.StandardUser).CanPlant);
        var asOwner = Evaluate(facts, owner);
        Assert.False(asOwner.CanAddFiles);
        Assert.True(asOwner.WriteDacFromOwnership);
    }

    [Fact]
    public void A_deny_overrides_an_allow_on_a_real_folder()
    {
        var path = _tree.Folder("deny");
        _tree.SetAcl(path, $"D:PAI(D;;0x2;;;BU)(A;OICI;FA;;;{Me})(A;;0x1301bf;;;WD)");

        var result = Evaluate(_probe.Probe(path, false), TokenPerspectives.StandardUser);

        Assert.False(result.CanAddFiles);
        Assert.True(result.CanAddSubdirectories);
    }

    [Fact]
    public void Writability_inherited_from_the_parent_is_traced_to_an_inherited_ACE()
    {
        var parent = _tree.Folder("parent");
        _tree.SetAcl(parent, $"D:PAI(A;OICI;FA;;;{Me})(A;OICI;FA;;;SY)(A;OICI;0x1301bf;;;BU)");
        var child = _tree.Folder(@"parent\child");

        var result = Evaluate(_probe.Probe(child, false), TokenPerspectives.StandardUser);

        Assert.True(result.CanAddFiles);
        var ace = Assert.Single(result.GrantedBy);
        Assert.Equal(WellKnownSids.Users, ace.Sid);
        Assert.True(ace.Inherited);
    }

    [Fact]
    public void A_junction_is_read_as_a_link_and_its_target_recorded()
    {
        var target = _tree.Folder("real");
        var link = _tree.PathOf("junction");
        _tree.Junction(link, target);

        var facts = _probe.Probe(link, false);

        Assert.True(facts.Exists);
        Assert.True(facts.IsReparsePoint);
        Assert.True(facts.IsJunction);
        Quiet.Same(target, facts.ReparseTarget, "the junction target");
        Assert.False(facts.ReparseTargetIsNetwork);
        Assert.NotNull(facts.Sddl);
    }

    [Fact]
    public void A_symlink_to_the_network_is_recorded_and_never_followed()
    {
        var link = _tree.PathOf("to-network");
        if (!_tree.TrySymlink(link, NetworkTarget))
        {
            output.WriteLine("skipped: this machine can't create symlinks unelevated (Developer Mode is off)");
            return;
        }

        var facts = _probe.Probe(link, false);
        Assert.True(facts.IsSymlink);
        Assert.Equal(NetworkTarget, facts.ReparseTarget, ignoreCase: true);
        Assert.True(facts.ReparseTargetIsNetwork);

        // Anything reached *through* the link would mean opening the share, so it isn't touched.
        var through = _probe.Probe(link + @"\bin", false);
        Assert.Equal(ProbeStatus.SkippedNetwork, through.Status);
        Assert.False(through.Exists);
    }

    [Fact]
    public void A_relative_symlink_resolves_against_its_folder()
    {
        var target = _tree.Folder("rel-target");
        var link = _tree.PathOf(@"sub\rel-link");
        _tree.Folder("sub");
        if (!_tree.TrySymlink(link, @"..\rel-target"))
        {
            output.WriteLine("skipped: this machine can't create symlinks unelevated (Developer Mode is off)");
            return;
        }

        Quiet.Same(target, _probe.Probe(link, false).ReparseTarget, "the resolved symlink target");
    }

    [Theory]
    [InlineData(NetworkTarget + @"\bin")]
    [InlineData(@"\\?\UNC\pathology-test.invalid\share\bin")]
    public void A_UNC_path_is_classified_without_being_opened(string path)
    {
        var started = DateTime.UtcNow;
        var facts = _probe.Probe(path, allowNetwork: false);

        Assert.Equal(ProbeStatus.SkippedNetwork, facts.Status);
        Assert.Equal(DriveKind.Unc, facts.Drive);
        // A name lookup or SMB attempt would take far longer than this.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.Equal(ProbeStatus.Failed, _probe.Probe(@"relative\bin", false).Status);
    }

    [Fact]
    public void An_8_3_path_reports_its_long_form()
    {
        var path = _tree.Folder("a folder with a long name");
        var shortPath = ShortName(path);
        if (shortPath is null || !shortPath.Contains('~'))
        {
            output.WriteLine("skipped: 8.3 names are disabled on this volume");
            return;
        }

        Quiet.Same(path, _probe.Probe(shortPath, false).LongPath, "the long path");
    }

    static unsafe string? ShortName(string path)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            var n = GetShortPathNameW(path, p, 1024);
            return n == 0 ? null : new string(p, 0, (int)n);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern unsafe uint GetShortPathNameW(string longPath, char* shortPath, uint size);

    public void Dispose()
    {
        _evaluator.Dispose();
        _tree.Dispose();
    }
}
