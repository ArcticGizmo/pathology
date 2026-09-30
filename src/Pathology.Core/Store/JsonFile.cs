using System.Text.Json;

namespace Pathology.Core.Store;

/// <summary>
/// Read/write JSON to disk with an atomic write (temp file + move), so an interrupted write
/// never leaves a half-written store on disk.
/// </summary>
internal static class JsonFile
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Read and deserialize; returns <c>default</c> if the file does not exist or is corrupt.</summary>
    public static T? Read<T>(string path) => Read<T>(path, Options);

    /// <summary>As <see cref="Read{T}(string)"/>, with caller-supplied serializer options.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options); }
        catch (JsonException) { return default; }   // a mangled store reads as "no store", not a crash
    }

    /// <summary>Serialize and write atomically (temp file in the same dir, then move over the target).</summary>
    public static void Write<T>(string path, T value) => Write(path, value, Options);

    /// <summary>As <see cref="Write{T}(string,T)"/>, with caller-supplied serializer options.</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, options));

        // Windows can transiently deny a rename-over-existing when an external handle (AV,
        // search indexer) briefly holds the destination. Retry with backoff, then clean up.
        try { MoveWithRetry(tmp, path); }
        finally { if (File.Exists(tmp)) TryDelete(tmp); }
    }

    static void MoveWithRetry(string tmp, string path)
    {
        const int attempts = 10;
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts - 1)
            {
                Thread.Sleep(15 * (i + 1));
            }
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* leftover temp is harmless */ }
    }
}
