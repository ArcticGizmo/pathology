using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pathology.Core.Model;
using Pathology.Core.Remediation;

namespace Pathology.Windows;

/// <summary>
/// Runs an <see cref="ElevatedBatch"/> through the elevated helper: this same exe, started with <c>runas</c> (one UAC
/// prompt) as <c>pathology apply-elevated &lt;batch&gt; &lt;sha256&gt; &lt;pipe&gt;</c>. <b>A writer's front door</b>:
/// built only by <c>AppServices</c>, run only from an apply the user clicked, never speculatively.
/// </summary>
/// <remarks>
/// <para>The batch is written to the app's own store and its SHA-256 goes on the command line, so the helper
/// applies exactly what was confirmed, or nothing.</para>
/// <para>The helper writes no file. It reports over a named pipe this side creates first, connecting as an
/// anonymous client so this side can't borrow its elevated token. The report isn't trusted either: every write
/// is read back from here and judged by what's actually there.</para>
/// </remarks>
public sealed class ElevatedHelperLauncher(string exePath, string pendingDirectory, IPathValueStore values, IAclStore acls) : IElevatedRunner
{
    public const string Verb = "apply-elevated";
    const int ErrorCancelled = 1223;

    public ElevatedResult Run(ElevatedBatch batch)
    {
        Directory.CreateDirectory(pendingDirectory);
        var file = Path.Combine(pendingDirectory, $"{batch.Id:N}.json");
        var bytes = ElevatedApplier.Serialize(batch);
        File.WriteAllBytes(file, bytes);
        var pipe = ElevatedHelper.PipeName(batch.Id);
        try
        {
            using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var start = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{Verb} \"{file}\" {ElevatedApplier.Hash(bytes)} {pipe}",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process? process;
            try { process = Process.Start(start); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return new ElevatedResult { Cancelled = true }; }
            if (process is null) return new ElevatedResult { Error = "The elevated helper didn't start." };

            using (process)
            {
                var report = Listen(server, process);
                return Verify(batch, report, process.ExitCode);
            }
        }
        finally
        {
            try { File.Delete(file); } catch { /* a leftover batch is inert: its hash only matches itself */ }
        }
    }

    /// <summary>Wait for the helper to finish, then take whatever it reported (null if nothing came).</summary>
    static ElevatedResult? Listen(NamedPipeServerStream server, Process process)
    {
        using var cancel = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancel.Token);
            using var buffer = new MemoryStream();
            await server.CopyToAsync(buffer, cancel.Token);
            return buffer.ToArray();
        });

        process.WaitForExit();
        try
        {
            if (!read.Wait(TimeSpan.FromSeconds(3))) { cancel.Cancel(); return null; }
            return JsonSerializer.Deserialize<ElevatedResult>(read.Result, PathSnapshotJson.Options);
        }
        catch (Exception ex) when (ex is AggregateException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Each planned write, judged by reading it back: what's there is what happened, whatever was reported.</summary>
    ElevatedResult Verify(ElevatedBatch batch, ElevatedResult? report, int exitCode)
    {
        var error = report?.Error ?? (report is null ? $"The elevated helper didn't report back (exit code {exitCode})." : null);
        var steps = new List<StepResult>();

        if (batch.Machine is { } machine)
        {
            var landed = Try(() => Steps.Same(values.Read(PathScope.Machine), machine.After));
            steps.Add(Judge(StepResult.MachineTarget, landed, report));
        }
        foreach (var acl in batch.Acls)
        {
            var landed = Try(() => acls.SameOwnPermissions(acl.AfterSddl!, acls.Read(acl.Path)));
            steps.Add(Judge(acl.Path, landed, report));
        }
        return new ElevatedResult { Steps = steps, Error = error };
    }

    static StepResult Judge(string target, bool landed, ElevatedResult? report)
    {
        var said = report?.Steps.FirstOrDefault(s => string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase));
        if (landed) return new(target, StepStatus.Applied);
        if (said is { Status: StepStatus.Applied }) return new(target, StepStatus.Failed, "Reported as applied, but it doesn't read back that way.");
        if (said is not null) return said;
        return new(target, report?.Error is not null ? StepStatus.Skipped : StepStatus.Failed, report is null ? "The elevated helper didn't report back." : null);
    }

    static bool Try(Func<bool> check)
    {
        try { return check(); }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// The elevated side: <c>pathology apply-elevated &lt;batch&gt; &lt;sha256&gt; &lt;pipe&gt;</c>. Refuses to run unelevated,
/// applies the batch through <see cref="ElevatedApplier"/> (which checks the hash and what the batch asks for), and
/// reports back over the pipe. It never writes a file.
/// </summary>
public static partial class ElevatedHelper
{
    public static string PipeName(Guid id) => $"pathology-{id:N}";

    [GeneratedRegex("^pathology-[0-9a-f]{32}$")]
    private static partial Regex PipePattern();

    public static int Run(string[] args)
    {
        if (args.Length != 3) return 2;
        var (file, hash, pipe) = (args[0], args[1], args[2]);

        ElevatedResult result;
        try
        {
            if (!Privileges.IsElevated)
                result = new ElevatedResult { Error = "The helper wasn't running elevated, so nothing was written." };
            else
            {
                var bytes = File.ReadAllBytes(file);
                Privileges.EnableForRepair();
                result = ElevatedApplier.Run(bytes, hash, new RegistryPathValueStore(), new SecurityDescriptorStore());
            }
        }
        catch (Exception ex)
        {
            result = new ElevatedResult { Error = ex.Message };
        }

        Report(pipe, result);
        return result.Error is null && result.Steps.All(s => s.Status == StepStatus.Applied) ? 0 : 1;
    }

    static void Report(string pipe, ElevatedResult result)
    {
        if (!PipePattern().IsMatch(pipe)) return;
        try
        {
            // Anonymous: whoever is on the other end learns nothing about this elevated token and can't impersonate it.
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Anonymous);
            client.Connect(5000);
            client.Write(JsonSerializer.SerializeToUtf8Bytes(result, PathSnapshotJson.Options));
        }
        catch (Exception)
        {
            // The app reads every write back anyway.
        }
    }
}
