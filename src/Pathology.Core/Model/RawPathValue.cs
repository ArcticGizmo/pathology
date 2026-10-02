using System.Text.Json.Serialization;

namespace Pathology.Core.Model;

/// <summary>A PATH value exactly as stored in the registry: unexpanded, with its value kind.</summary>
public sealed record RawPathValue
{
    public PathScope Scope { get; init; }

    /// <summary>The stored string, unexpanded. Null when <see cref="Kind"/> is <see cref="PathValueKind.Missing"/>.</summary>
    public string? Value { get; init; }

    public PathValueKind Kind { get; init; }

    /// <summary>Characters in the stored value (the <c>setx</c> 1024-truncation and length checks read this).</summary>
    public int Length => Value?.Length ?? 0;

    /// <summary>True when Windows expands <c>%VAR%</c> references in this value.</summary>
    [JsonIgnore] public bool Expands => Kind == PathValueKind.ExpandString;

    public static RawPathValue Missing(PathScope scope) => new() { Scope = scope, Kind = PathValueKind.Missing };
}
