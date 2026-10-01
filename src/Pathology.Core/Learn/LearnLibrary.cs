using Pathology.Core.Detection;

namespace Pathology.Core.Learn;

/// <summary>One explainer on the Learn page. <see cref="Markdown"/> is the body (no title heading).</summary>
public sealed record LearnArticle(string Id, string Title, string Summary, string Markdown);

/// <summary>
/// The Learn articles: short notes on how Windows really resolves commands and DLLs, which findings link to by
/// <see cref="LearnTopics"/> id. The bodies are markdown files embedded in this assembly (<c>Learn/Articles</c>).
/// </summary>
public static class LearnLibrary
{
    static readonly (string Id, string Title, string Summary)[] Catalog =
    [
        (LearnTopics.DllSearchOrder, "DLL search order",
            "Why a folder SYSTEM searches is a way in, even when nothing in it is run directly."),
        (LearnTopics.PathExt, "How a command is found",
            "PATH order, PATHEXT order, and why where.bat in the wrong folder beats where.exe."),
        (LearnTopics.UacAndPath, "UAC and PATH",
            "Your elevated programs search your PATH too, folders you can write included."),
        (LearnTopics.NewProcessPath, "How Windows builds a new process's PATH",
            "Machine then user, expanded once, and inherited from whoever started the program."),
        (LearnTopics.ValueKinds, "REG_SZ and REG_EXPAND_SZ",
            "Why %SystemRoot% sometimes stays %SystemRoot%."),
        (LearnTopics.PhantomDirectories, "Phantom directories",
            "A PATH folder that doesn't exist yet is one anyone allowed to create it can fill."),
        (LearnTopics.WhyNotSetx, "Why not setx",
            "The classic one-liner that freezes, duplicates and truncates your PATH."),
    ];

    static readonly Lazy<IReadOnlyList<LearnArticle>> Articles = new(() =>
        Catalog.Select(c => new LearnArticle(c.Id, c.Title, c.Summary, Load(c.Id))).ToList());

    /// <summary>Every article, in reading order.</summary>
    public static IReadOnlyList<LearnArticle> All => Articles.Value;

    /// <summary>The article with this id, or null.</summary>
    public static LearnArticle? Find(string id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    static string Load(string id)
    {
        using var stream = typeof(LearnLibrary).Assembly.GetManifestResourceStream($"Pathology.Learn.{id}.md");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
