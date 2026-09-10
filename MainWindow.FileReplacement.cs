using System;
using System.IO;

namespace lifeviz;

public partial class MainWindow
{
    internal bool ReplaceFileSourceFromEditor(Guid sourceId, string path, out string? error)
    {
        var source = FindSource(s => s.Id == sourceId);
        if (source == null)
        {
            error = "The layer no longer exists.";
            return false;
        }

        if (!TryReplaceFileSource(source, path, out error))
        {
            return false;
        }

        ApplySourceVideoAudioState(source);
        ApplySourceDecodeActivation();
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        RebuildSourcesMenu();
        return true;
    }

    private bool TryReplaceFileSource(CaptureSource source, string path, out string? error)
    {
        error = null;
        if (source.Type != CaptureSource.SourceType.File ||
            source.FilePath?.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) == true)
        {
            error = "Select a local file layer to replace its file.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.Equals(source.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            source.SetDisplayName(Path.GetFileName(fullPath));
            return true;
        }

        // Direct file layers share sessions by path. Keep the existing one-layer-per-file
        // rule so replacing a layer cannot change another layer's transport or audio.
        if (FindSource(s => s.Id != source.Id && s.Type == CaptureSource.SourceType.File &&
            string.Equals(s.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) != null)
        {
            error = "That file is already used by another file layer. Choose a different file.";
            return false;
        }

        // Open first: a missing or unsupported replacement must leave the old layer intact.
        if (!_fileCapture.TryGetOrAdd(fullPath, out var info, out error))
        {
            return false;
        }

        string? oldPath = source.FilePath;
        source.ReplaceFile(info);
        if (oldPath != null && FindSource(s => s.Id != source.Id &&
            s.Type == CaptureSource.SourceType.File &&
            string.Equals(s.FilePath, oldPath, StringComparison.OrdinalIgnoreCase)) == null)
        {
            _fileCapture.Remove(oldPath);
        }
        return true;
    }
}
