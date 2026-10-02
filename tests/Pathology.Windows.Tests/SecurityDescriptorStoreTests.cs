using System.Security.AccessControl;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Windows.Tests;

/// <summary>
/// The ACL writer, on folders a <see cref="TempTree"/> created and nowhere else (see CLAUDE.md). A lock-down is
/// designed, written, read back, carried to a subfolder by inheritance, and put back.
/// </summary>
public sealed class SecurityDescriptorStoreTests : IDisposable
{
    readonly TempTree _tree = new();
    readonly SecurityDescriptorStore _sut = new();
    readonly AclDesigner _designer = new();
    readonly AccessEvaluator _evaluator = new();

    static string Me => TempTree.CurrentUserSid;

    /// <summary>A folder anyone may modify (the drive-root shape, explicit here), with a subfolder that inherits it.</summary>
    (string Folder, string Child) Writable()
    {
        var folder = _tree.Folder("tools");
        var child = _tree.Folder(@"tools\sub");
        _tree.SetAcl(folder, $"D:PAI(A;OICI;FA;;;{Me})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;AU)");
        return (folder, child);
    }

    bool AnyoneCanPlant(string path) => _evaluator.Evaluate(_sut.Read(path), TokenPerspectives.StandardUser).CanPlant;

    AclDesign LockDown(string path) =>
        _designer.Design(path, _sut.Read(path), new AclFix { Folder = path, Scope = PathScope.User, StripWrite = true, KeepWriteSid = Me }, AclDesignMode.Protect);

    [Fact]
    public void A_lock_down_is_written_read_back_and_reaches_the_subfolder()
    {
        var (folder, child) = Writable();
        Assert.True(AnyoneCanPlant(folder));
        Assert.True(AnyoneCanPlant(child));

        var design = LockDown(folder);
        _sut.Write(folder, design.Sddl!);

        Assert.True(_sut.SameOwnPermissions(design.Sddl!, _sut.Read(folder)));
        Assert.False(AnyoneCanPlant(folder));
        // The subfolder only inherits, so the change reached it the way icacls's would.
        Assert.False(AnyoneCanPlant(child));
    }

    [Fact]
    public void Putting_the_old_permissions_back_restores_them_and_inheritance()
    {
        var folder = _tree.Folder("inherits");
        var before = _sut.Read(folder);
        Assert.Contains("ID;", before);

        _sut.Write(folder, LockDown(folder).Sddl!);
        Assert.True(new RawSecurityDescriptor(_sut.Read(folder)).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));

        _sut.Write(folder, before);

        var restored = _sut.Read(folder);
        Assert.True(_sut.SameOwnPermissions(before, restored));
        Assert.False(new RawSecurityDescriptor(restored).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Contains("ID;", restored);
    }

    [Fact]
    public void Own_permissions_ignore_what_is_inherited()
    {
        Assert.True(_sut.SameOwnPermissions(
            "O:BAD:AI(A;OICI;FA;;;SY)(A;ID;0x1301bf;;;AU)",
            "O:BAD:AI(A;OICI;FA;;;SY)(A;OICIID;0x1200a9;;;AU)"));
        Assert.False(_sut.SameOwnPermissions("O:BAD:AI(A;OICI;FA;;;SY)", "O:BAD:PAI(A;OICI;FA;;;SY)"));
        Assert.False(_sut.SameOwnPermissions("O:BAD:(A;OICI;FA;;;SY)", "O:SYD:(A;OICI;FA;;;SY)"));
        Assert.False(_sut.SameOwnPermissions("O:BAD:(A;OICI;FA;;;SY)", "O:BAD:(A;OICI;FA;;;SY)(A;;FA;;;WD)"));
    }

    [Fact]
    public void A_junction_is_refused_and_its_target_left_alone()
    {
        var target = _tree.Folder("target");
        var link = _tree.PathOf("link");
        _tree.Junction(link, target);
        var before = _sut.Read(target);

        Assert.Throws<InvalidOperationException>(() => _sut.Read(link));
        Assert.Throws<InvalidOperationException>(() => _sut.Write(link, before));
        Assert.Throws<InvalidOperationException>(() => _sut.Write(Path.Combine(link, "inside"), before));
        Quiet.Same(before, _sut.Read(target), "the target's SDDL");
    }

    [Theory]
    [InlineData(@"\\server\share\tools")]
    [InlineData(@"relative\tools")]
    [InlineData(@"C:\Tools\..\Windows")]
    public void Anything_but_a_plain_local_path_is_refused_without_being_opened(string path)
    {
        Assert.Throws<InvalidOperationException>(() => _sut.Read(path));
        Assert.Throws<InvalidOperationException>(() => _sut.Write(path, "O:BAD:(A;;FA;;;SY)"));
    }

    public void Dispose()
    {
        _evaluator.Dispose();
        _tree.Dispose();
    }
}
