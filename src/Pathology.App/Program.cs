using Avalonia;
using Pathology.App.Cli;
using Pathology.App.Rendering;
using Pathology.App.Updates;
using Velopack;

namespace Pathology.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install/update lifecycle hook — must run before anything else. No-op unless
        // launched with the special --veloapp-* hook args (i.e. during install/update). `vpk pack`
        // verifies this is the first call in Main, so it must stay first.
        VelopackApp.Build().Run();

        // `pathology render <dir>` dumps every page to PNG (headless) for visual verification. Read-only.
        if (args.Length > 0 && args[0] == "render")
            return HeadlessRenderer.RenderAll(args.Length > 1 ? args[1] : ".");

        // `pathology snapshot [file]` scans this machine (read-only, no network) and writes a redacted snapshot:
        // fixture material for the detectors, and the bug-report export. Prints counts only.
        if (args.Length > 0 && args[0] == "snapshot")
            return SnapshotCommand.Run(args.Length > 1 ? args[1] : null);

        // `pathology scan [--redact] [--details] [--from file]` diagnoses this machine (read-only, no network) and prints
        // the findings. A dev and verification aid; exit code = Critical + High problems.
        if (args.Length > 0 && args[0] == "scan")
            return ScanCommand.Run(args[1..]);

        // `pathology check-update` runs the notify-only update check and prints the result — a headless
        // probe of the same path the UI uses on launch (honours PATHOLOGY_UPDATE_FEED).
        if (args.Length > 0 && args[0] == "check-update")
        {
            var notice = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine(notice ?? "up to date (or not installed via Velopack)");
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
