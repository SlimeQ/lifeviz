using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal (bool ok, string detail) RunGifSceneRecoverySmoke()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lifeviz-gif-scene-smoke-{Guid.NewGuid():N}");
        string gifPath = Path.Combine(tempDirectory, "corrupt-scene.gif");
        string configPath = ConfigPath;
        FlushPendingConfigSave();
        byte[]? priorConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        bool priorConfigReady = _configReady;
        string? priorLastPersistedConfigJson = _lastPersistedConfigJson;

        try
        {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllBytes(gifPath, "not a gif"u8.ToArray());
            ClearSources(persist: false);

            _configReady = true;
            _configSaveDirty = false;
            _lastPersistedConfigJson = null;
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }

            if (!_fileCapture.TryGetOrAdd(gifPath, out var info, out string? error))
            {
                return (false, error ?? "Corrupt GIF did not enter a pending session.");
            }

            var source = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
            _sources.Add(source);
            bool runtimeNodeCreated = _sources.Contains(source) &&
                                      _fileCapture.IsStreamingSource(gifPath);
            SaveConfig();
            FlushPendingConfigSave();
            bool persistedWithSource = ConfigContainsGifSmokePath(configPath, gifPath);
            bool reachedError = SpinWait.SpinUntil(
                () => _fileCapture.GetState(gifPath) == FileCaptureService.FileCaptureState.Error,
                millisecondsTimeout: 18_000);
            if (!reachedError)
            {
                return (false, "Corrupt GIF never reached a terminal error state.");
            }

            source.AddedUtc = DateTime.UtcNow - TimeSpan.FromSeconds(20);
            source.MissedFrames = 181;
            InjectCaptureFrames(injectLayers: false);
            FlushPendingConfigSave();

            bool removedFromScene = !_sources.Contains(source) &&
                                    !_fileCapture.IsStreamingSource(gifPath);
            bool persistedWithoutSource = false;
            if (File.Exists(configPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
                persistedWithoutSource = document.RootElement.TryGetProperty("Sources", out var sources) &&
                                         sources.ValueKind == JsonValueKind.Array &&
                                         sources.GetArrayLength() == 0 &&
                                         !ConfigContainsGifSmokePath(configPath, gifPath);
            }

            bool ok = runtimeNodeCreated &&
                      persistedWithSource &&
                      reachedError &&
                      removedFromScene &&
                      persistedWithoutSource;
            return (ok,
                $"node={runtimeNodeCreated}, persistedWithSource={persistedWithSource}, " +
                $"error={reachedError}, removed={removedFromScene}, " +
                $"persistedWithoutSource={persistedWithoutSource}");
        }
        catch (Exception ex)
        {
            return (false, ex.ToString());
        }
        finally
        {
            try
            {
                ClearSources(persist: false);
                FlushPendingConfigSave();
                if (priorConfig == null)
                {
                    if (File.Exists(configPath))
                    {
                        File.Delete(configPath);
                    }
                }
                else
                {
                    string? configDirectory = Path.GetDirectoryName(configPath);
                    if (!string.IsNullOrWhiteSpace(configDirectory))
                    {
                        Directory.CreateDirectory(configDirectory);
                    }
                    File.WriteAllBytes(configPath, priorConfig);
                }

                _configReady = priorConfigReady;
                _configSaveDirty = false;
                _lastPersistedConfigJson = priorLastPersistedConfigJson;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not restore GIF scene smoke state: {ex.Message}");
            }

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not remove GIF scene smoke directory: {ex.Message}");
            }
        }
    }

    private static bool ConfigContainsGifSmokePath(string configPath, string gifPath)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!document.RootElement.TryGetProperty("Sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (source.TryGetProperty("FilePath", out var filePath) &&
                string.Equals(filePath.GetString(), gifPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
