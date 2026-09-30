using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Pathology.App.Changelog;
using Pathology.App.ViewModels;
using Pathology.App.Views;
using Pathology.Core.Changelog;

namespace Pathology.App.Rendering;

/// <summary>
/// Renders the app's views to PNG on a headless Skia platform, so the UI can be eyeballed without a
/// display. Invoked via <c>pathology render &lt;dir&gt;</c> (by convention <c>./captures/render</c>).
/// </summary>
/// <remarks>
/// <b>Read-only by construction.</b> It builds real view-models over a throwaway temp store, and when a page
/// needs a state to render interestingly (an update available, from M4 a posed scan) that state is set on
/// the view-model directly — never produced by running an operation.
/// </remarks>
internal static class HeadlessRenderer
{
    public static int RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            // A temp store root, so a render never reads or writes the real settings file.
            var root = Path.Combine(Path.GetTempPath(), "pathology-render-" + Guid.NewGuid().ToString("N"));
            try
            {
                var services = new AppServices(root);
                var vm = new MainWindowViewModel(services);

                foreach (var page in vm.Pages)
                {
                    vm.CurrentPage = page;
                    Capture(vm, Path.Combine(outDir, $"main_{page.Title.ToLowerInvariant()}.png"));
                }

                RenderChangelog(outDir);
                RenderUpdateButton(outDir, services);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            }

            Console.WriteLine($"rendered to {Path.GetFullPath(outDir)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The "what's new" window (the post-update popup / About viewer), over the real changelog.</summary>
    static void RenderChangelog(string outDir)
    {
        var markdown = ChangelogMarkdown.LoadEmbedded();
        var sections = markdown is null ? [] : ChangelogParser.Parse(markdown);
        var window = new ChangelogWindow("What's new in PATHology", "Recent releases", sections);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "changelog.png"));
        window.Close();
    }

    /// <summary>
    /// The "Update to v…" call-to-action in the nav, posed with a version so it's visible: a dev or headless
    /// build is never Velopack-installed, so a real check reports "not applicable" and the button hides. The
    /// version is set directly — a read-only pose, nothing is downloaded or applied.
    /// </summary>
    static void RenderUpdateButton(string outDir, AppServices services)
    {
        var vm = new MainWindowViewModel(services) { AvailableVersion = "0.2.0" };
        Capture(vm, Path.Combine(outDir, "update_available.png"));
    }

    static void Capture(MainWindowViewModel vm, string path)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(path);
        window.Close();
    }
}
