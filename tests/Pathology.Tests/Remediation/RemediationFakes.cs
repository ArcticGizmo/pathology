using Pathology.Core.Capture;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Tests.Remediation;

/// <summary>
/// Stand-ins for the writers, so the apply pipeline is tested without touching the registry or a real ACL (see
/// CLAUDE.md). Values and SDDLs live in dictionaries.
/// </summary>
internal sealed class FakeDesigner : IAclDesigner
{
    public List<(string Path, AclDesignMode Mode)> Calls { get; } = [];
    public string? RefuseFor { get; set; }

    public AclDesign Design(string path, string sddl, AclFix fix, AclDesignMode mode)
    {
        Calls.Add((path, mode));
        if (string.Equals(path, RefuseFor, StringComparison.OrdinalIgnoreCase)) return new AclDesign { Refusal = "too odd" };
        return new AclDesign
        {
            Sddl = $"{sddl}|{mode}",
            OwnerSid = fix.ResetOwner ? WellKnownSids.Administrators : null,
            Lines = [$"{mode} {path}"],
            Commands = [$"icacls \"{path}\" …"],
        };
    }
}

/// <summary>Grants read-only access for any SDDL the designer made; asks of anything else are recorded.</summary>
internal sealed class LockedEvaluator : IAccessEvaluator
{
    public List<string> Asked { get; } = [];

    public AccessResult Evaluate(string sddl, PerspectiveIdentity identity)
    {
        Asked.Add(sddl);
        return new AccessResult { Perspective = identity.Perspective, Granted = FileAccessRights.ListDirectory | FileAccessRights.Traverse };
    }
}

internal sealed class FakeValues : IPathValueStore
{
    public Dictionary<PathScope, RawPathValue> Stored { get; } = new()
    {
        [PathScope.Machine] = RawPathValue.Missing(PathScope.Machine),
        [PathScope.User] = RawPathValue.Missing(PathScope.User),
    };

    public List<RawPathValue> Writes { get; } = [];

    /// <summary>Runs before each write lands (to check what's on disk by then).</summary>
    public Action<RawPathValue>? BeforeWrite { get; set; }

    /// <summary>Store something other than what was asked, to test the read-back.</summary>
    public bool Garble { get; set; }

    public FakeValues With(PathSnapshot snapshot)
    {
        Stored[PathScope.Machine] = snapshot.MachinePath;
        Stored[PathScope.User] = snapshot.UserPath;
        return this;
    }

    public RawPathValue Read(PathScope scope) => Stored[scope];

    public void Write(RawPathValue value)
    {
        BeforeWrite?.Invoke(value);
        Writes.Add(value);
        Stored[value.Scope] = Garble ? value with { Value = value.Value + "?" } : value;
    }
}

internal sealed class FakeAcls : IAclStore
{
    public Dictionary<string, string> Sddl { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, string Sddl)> Writes { get; } = [];
    public string? FailFor { get; set; }

    public string Read(string path) => Sddl.TryGetValue(path, out var s) ? s : throw new IOException("no such folder");

    public bool SameOwnPermissions(string expected, string actual) => expected == actual;

    public void Write(string path, string sddl)
    {
        if (string.Equals(path, FailFor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Access is denied.");
        Writes.Add((path, sddl));
        Sddl[path] = sddl;
    }
}

internal sealed class FakeBroadcast : IEnvironmentBroadcast
{
    public int Count { get; private set; }

    public bool Broadcast()
    {
        Count++;
        return true;
    }
}

/// <summary>
/// The elevated helper without the UAC prompt: the batch is serialised, hashed and handed to the real
/// <see cref="ElevatedApplier"/> over the same fakes, so the hand-over is tested end to end.
/// </summary>
internal sealed class FakeElevation(FakeValues values, FakeAcls acls) : IElevatedRunner
{
    public bool Decline { get; set; }
    public List<ElevatedBatch> Batches { get; } = [];

    public ElevatedResult Run(ElevatedBatch batch)
    {
        Batches.Add(batch);
        if (Decline) return new ElevatedResult { Cancelled = true };
        var bytes = ElevatedApplier.Serialize(batch);
        return ElevatedApplier.Run(bytes, ElevatedApplier.Hash(bytes), values, acls);
    }
}

internal sealed class MemoryHistory : IChangeHistory
{
    public Dictionary<Guid, ChangeRecord> Records { get; } = [];
    public List<ChangeOutcome> Saves { get; } = [];

    public void Save(ChangeRecord record)
    {
        Records[record.Id] = record;
        Saves.Add(record.Outcome);
    }

    public IReadOnlyList<ChangeRecord> All() => Records.Values.OrderByDescending(r => r.StartedAt).ToList();
    public ChangeRecord? Get(Guid id) => Records.GetValueOrDefault(id);
}

/// <summary>A probe that knows nothing and records what it was asked.</summary>
internal sealed class RecordingProbe : IDirectoryProbe
{
    public List<string> Probed { get; } = [];

    public DirectoryFacts Probe(string path, bool allowNetwork)
    {
        Probed.Add(path);
        return new DirectoryFacts { Path = path, Status = ProbeStatus.Probed, Drive = DriveKind.Fixed, Exists = true, IsDirectory = true };
    }

    public IReadOnlyList<string>? ListFiles(string path, IReadOnlySet<string> extensions, bool allowNetwork) => ["new.exe"];
}
