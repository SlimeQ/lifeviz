using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace lifeviz;

// Disk persistence is independent of WPF and GPU/media lifetimes. Each replacement
// preserves its predecessor, and competing processes cannot silently overwrite it.
internal static class SceneFileStore
{
    internal const int HistoryLimit = 40;
    internal static string HistoryDirectory(string path) => path + ".history";

    internal static void ValidateSceneJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The file must contain a scene Sources array.");
        ValidateSources(sources);
    }

    private static void ValidateSources(JsonElement sources)
    {
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A scene source is missing its type.");
            if (source.TryGetProperty("Children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Source children must be an array.");
                ValidateSources(children);
            }
        }
    }

    internal static IEnumerable<string> RecoveryFiles(string path)
    {
        yield return path;
        yield return path + ".bak";
        string directory = HistoryDirectory(path);
        if (Directory.Exists(directory))
            foreach (string file in Directory.GetFiles(directory, "*.json")
                .Where(file => !Path.GetFileName(file).StartsWith("unsaved-", StringComparison.Ordinal) &&
                               !Path.GetFileName(file).StartsWith("editor-draft-", StringComparison.Ordinal))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
                yield return file;
    }

    internal static (string? json, string? source) ReadRecoverable(string path, Action<string> validate)
    {
        bool found = false;
        foreach (string candidate in RecoveryFiles(path))
        {
            if (!File.Exists(candidate)) continue;
            found = true;
            try
            {
                string json = File.ReadAllText(candidate);
                validate(json);
                return (json, candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn($"Scene recovery skipped {candidate}: {ex.Message}");
            }
        }
        if (found) throw new InvalidDataException("No readable scene revision was found. Existing files have been preserved.");
        return (null, null);
    }

    internal static void Write(string path, string json, string? expectedJson = null, bool checkForConflict = false)
    {
        ValidateSceneJson(json);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The lock file stays on disk; the exclusive handle is the lock. Do not
        // delete it on release, which would race another process opening it.
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string? previous = File.Exists(path) ? File.ReadAllText(path) : null;
        if (checkForConflict && !string.Equals(previous, expectedJson, StringComparison.Ordinal))
            throw new SceneSaveConflictException("Another LifeViz session or program changed this scene. Export your scene before restarting; autosave will not overwrite the other revision.");
        if (string.Equals(previous, json, StringComparison.Ordinal)) return;

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(temp, json);
            if (previous != null)
            {
                string history = HistoryDirectory(path);
                Directory.CreateDirectory(history);
                // Preserve even an invalid predecessor for forensic/manual recovery.
                string extension = ".json";
                try { ValidateSceneJson(previous); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { extension = ".invalid"; }
                WriteDurably(Path.Combine(history, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-" + Guid.NewGuid().ToString("N") + extension), previous);
                File.Replace(temp, path, path + ".bak");
            }
            else
            {
                File.Move(temp, path);
            }
            // Retention failure must not report a successfully committed save as failed.
            try
            {
                string history = HistoryDirectory(path);
                if (Directory.Exists(history))
                    foreach (string old in Directory.GetFiles(history, "*.json")
                        .Where(file => char.IsDigit(Path.GetFileName(file)[0]))
                        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(HistoryLimit))
                        File.Delete(old);
            }
            catch (Exception ex) { Logger.Warn($"Could not trim scene history: {ex.Message}"); }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
        }
    }

    private static void WriteDurably(string path, string contents)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

internal sealed class SceneSaveConflictException(string message) : IOException(message);
