using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace lifeviz;

// A shareable scene: one zip holding the project JSON plus copies of every local
// media file it references. Paths inside the package are relative ("media/..."),
// and import rewrites them to an extraction folder on the recipient's machine.
internal static class ScenePackage
{
    internal const string Extension = ".lifevizscene";
    internal const string DialogFilter = "LifeViz Scene Package (*.lifevizscene)|*.lifevizscene";
    internal const int FormatVersion = 1;
    private const string SceneEntry = "scene.lifevizlayers.json";
    private const string ManifestEntry = "lifeviz-package.json";
    private const string MediaPrefix = "media/";
    private const string CompleteMarker = ".complete";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string DefaultImportRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lifeviz", "shared-scenes");

    internal static bool IsPackagePath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    internal sealed record MediaFile(string SourcePath, string EntryName, long Bytes);

    internal sealed class Plan
    {
        public LayerConfigFile Scene { get; init; } = new();
        public List<MediaFile> Media { get; } = new();
        // Things the recipient will not get from the package (live inputs, links, missing files).
        public List<string> NotIncluded { get; } = new();
        public long TotalBytes => Media.Sum(file => file.Bytes);
    }

    internal sealed class Manifest
    {
        public int FormatVersion { get; set; } = ScenePackage.FormatVersion;
        public Guid PackageId { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string AppVersion { get; set; } = string.Empty;
        public List<string> Media { get; set; } = new();
        public List<string> NotIncluded { get; set; } = new();
    }

    // Copies the scene (the original is not modified) with local media paths
    // replaced by package entry names, and lists what will be bundled.
    internal static Plan CreatePlan(LayerConfigFile scene, bool omitLiveInputs = false)
    {
        var copy = LayerConfigFile.Parse(JsonSerializer.Serialize(scene));
        if (omitLiveInputs) copy.Sources = WithoutLiveInputs(copy.Sources);
        var plan = new Plan { Scene = copy };
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Map(string? path, string usage)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return path;
            string full = Path.GetFullPath(path);
            if (byPath.TryGetValue(full, out string? existing)) return existing;
            if (!File.Exists(full))
            {
                plan.NotIncluded.Add($"Missing {usage}: {full}");
                return path;
            }
            string name = SafeFileName(Path.GetFileName(full));
            string entry = MediaPrefix + name;
            for (int i = 2; !usedNames.Add(entry); i++)
                entry = MediaPrefix + Path.GetFileNameWithoutExtension(name) + $" ({i})" + Path.GetExtension(name);
            byPath[full] = entry;
            plan.Media.Add(new MediaFile(full, entry, new FileInfo(full).Length));
            return entry;
        }

        foreach (var source in Enumerate(copy.Sources))
        {
            string label = string.IsNullOrWhiteSpace(source.DisplayName) ? source.Type ?? "layer" : source.DisplayName;
            if (IsLiveInput(source))
                plan.NotIncluded.Add($"{source.Type} \"{label}\" (live input from the sender's PC)");
            if (source.Type == nameof(LayerEditorSourceKind.Webcam))
            {
                // Camera IDs are specific to this PC, so shared webcam layers use the
                // recipient's first free camera; they can pick another in the layer.
                plan.NotIncluded.Add($"Webcam \"{label}\" (uses their camera; they can choose which one in the layer's Camera list)");
                source.WebcamId = null;
                source.DisplayName = MainWindow.AnyWebcamName;
            }
            if (source.FilePath?.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) == true)
                plan.NotIncluded.Add($"YouTube link \"{label}\" (streams online; not bundled)");
        }
        RewritePaths(copy, Map);
        return plan;
    }

    // Window captures depend on the sender's PC; on another PC they are missing
    // inputs, so the sender can choose to leave them out. Webcam layers travel as
    // "Any camera" layers instead (see CreatePlan).
    internal static bool IsLiveInput(LayerConfigSource source) =>
        source.Type is nameof(LayerEditorSourceKind.Window);

    internal static List<string> DescribeLiveInputs(LayerConfigFile scene) =>
        Enumerate(scene.Sources).Where(IsLiveInput)
            .Select(source => $"{source.Type}: {(string.IsNullOrWhiteSpace(source.DisplayName) ? source.WindowTitle ?? source.WebcamId : source.DisplayName)}")
            .ToList();

    private static List<LayerConfigSource> WithoutLiveInputs(IEnumerable<LayerConfigSource> sources) =>
        sources.Where(source => !IsLiveInput(source))
            .Select(source => { source.Children = WithoutLiveInputs(source.Children); return source; })
            .ToList();

    internal static void Write(Plan plan, string destination, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        destination = Path.GetFullPath(destination);
        string partial = destination + ".partial";
        long total = Math.Max(1, plan.TotalBytes);
        long copied = 0;
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = new Manifest
                {
                    Name = Path.GetFileNameWithoutExtension(destination),
                    AppVersion = AppVersionInfo.DisplayVersion,
                    Media = plan.Media.Select(file => file.EntryName).ToList(),
                    NotIncluded = plan.NotIncluded.ToList()
                };
                WriteText(archive, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions));
                WriteText(archive, SceneEntry, JsonSerializer.Serialize(plan.Scene, JsonOptions));
                var buffer = new byte[1 << 20];
                foreach (var file in plan.Media)
                {
                    // Media is already compressed; storing it keeps export fast.
                    var entry = archive.CreateEntry(file.EntryName, CompressionLevel.NoCompression);
                    using var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        copied += read;
                        progress?.Report(Math.Min(1.0, copied / (double)total));
                    }
                }
            }
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (IOException) { }
        }
    }

    internal sealed record ImportResult(LayerConfigFile Scene, string MediaFolder, IReadOnlyList<string> NotIncluded);

    // Extracts media to a per-package folder (re-importing the same package reuses
    // it) and returns the scene with paths pointing at the extracted files.
    internal static ImportResult Import(string packagePath, string? importRoot = null, IProgress<double>? progress = null)
    {
        importRoot = Path.GetFullPath(importRoot ?? DefaultImportRoot);
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry(ManifestEntry) ?? throw new InvalidDataException("This is not a LifeViz scene package.");
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadText(manifestEntry))
            ?? throw new InvalidDataException("The scene package manifest is empty.");
        if (manifest.FormatVersion > FormatVersion)
            throw new InvalidDataException("This scene package requires a newer version of LifeViz.");
        var sceneEntry = archive.GetEntry(SceneEntry) ?? throw new InvalidDataException("The scene package has no scene.");
        var scene = LayerConfigFile.Parse(ReadText(sceneEntry));

        string name = SafeFileName(string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(packagePath) : manifest.Name);
        string folder = Path.Combine(importRoot, $"{name}-{manifest.PackageId.ToString("N")[..8]}");
        var mediaEntries = archive.Entries.Where(entry => entry.FullName.StartsWith(MediaPrefix, StringComparison.Ordinal) && entry.Name.Length > 0).ToList();
        if (!IsComplete(folder, mediaEntries))
        {
            Directory.CreateDirectory(importRoot);
            string staging = folder + "." + Guid.NewGuid().ToString("N")[..8] + ".partial";
            try
            {
                long total = Math.Max(1, mediaEntries.Sum(entry => entry.Length));
                long done = 0;
                foreach (var entry in mediaEntries)
                {
                    string target = ResolveInside(staging, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    done += entry.Length;
                    progress?.Report(done / (double)total);
                }
                File.WriteAllText(Path.Combine(staging, CompleteMarker), manifest.PackageId.ToString());
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.Move(staging, folder);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
            }
        }

        RewritePaths(scene, (path, _) => path != null && path.StartsWith(MediaPrefix, StringComparison.Ordinal)
            ? ResolveInside(folder, path)
            : path);
        return new ImportResult(scene, folder, manifest.NotIncluded);
    }

    private static bool IsComplete(string folder, IEnumerable<ZipArchiveEntry> mediaEntries)
    {
        if (!File.Exists(Path.Combine(folder, CompleteMarker))) return false;
        foreach (var entry in mediaEntries)
        {
            var file = new FileInfo(ResolveInside(folder, entry.FullName));
            if (!file.Exists || file.Length != entry.Length) return false;
        }
        return true;
    }

    private static string ResolveInside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The scene package contains an invalid file path.");
        return full;
    }

    // Every media reference a scene can hold. Keep in sync with LayerConfigSource.
    private static void RewritePaths(LayerConfigFile scene, Func<string?, string, string?> map)
    {
        foreach (var source in Enumerate(scene.Sources))
        {
            source.FilePath = map(source.FilePath, "file");
            source.FilePaths = source.FilePaths.Select(path => map(path, "file") ?? path).ToList();
            foreach (var item in source.AutoClipVideoOverrides)
                item.FilePath = map(item.FilePath, "AutoClip video");
            if (source.MoviePlaylist != null)
                foreach (var movie in source.MoviePlaylist.Movies)
                {
                    movie.FilePath = map(movie.FilePath, "movie") ?? string.Empty;
                    movie.SubtitlePath = map(movie.SubtitlePath, "subtitle file");
                }
            if (source.ProjectM != null)
                source.ProjectM.Presets = source.ProjectM.Presets.Select(path => map(path, "MilkDrop preset") ?? path).ToList();
        }
    }

    private static IEnumerable<LayerConfigSource> Enumerate(IEnumerable<LayerConfigSource> sources)
    {
        foreach (var source in sources)
        {
            yield return source;
            foreach (var child in Enumerate(source.Children))
                yield return child;
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(safe) ? "scene" : safe;
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
