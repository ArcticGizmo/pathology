using Pathology.Core.Capture;

namespace Pathology.Windows;

/// <summary>The real, machine-facing capturer. Built only by the composition root (<c>AppServices</c>).</summary>
public static class WindowsCapture
{
    public static SnapshotCapturer Create(IAccessEvaluator evaluator) => new(
        new RegistryPathReader(),
        new EffectiveEnvironmentReader(),
        new HostInfoReader(),
        new TokenPerspectives(),
        new DirectoryProbe(),
        evaluator);
}
