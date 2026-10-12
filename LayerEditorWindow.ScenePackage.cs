using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace lifeviz;

// Share... writes a .lifevizscene package (scene + media); Load... imports one.
public partial class LayerEditorWindow
{
    private bool _scenePackageBusy;

    private async void ShareScene_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerIsShuttingDown || _scenePackageBusy) return;
        ScenePackage.Plan plan;
        try
        {
            var scene = BuildSceneForExport();
            bool omitLiveInputs = false;
            var liveInputs = ScenePackage.DescribeLiveInputs(scene);
            if (liveInputs.Count > 0)
            {
                var choice = MessageBox.Show(this,
                    "This scene captures windows on this PC, which can't be put in a package:\n\n" +
                    string.Join("\n", liveInputs.Take(12).Select(item => "• " + item)) +
                    (liveInputs.Count > 12 ? $"\n• …and {liveInputs.Count - 12} more" : "") +
                    "\n\nLeave them out of the shared scene?\n\n" +
                    "Yes: leave them out (recommended unless they have the same window open).\n" +
                    "No: keep them; they show up as missing inputs on the other PC.",
                    "Share Scene", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (choice == MessageBoxResult.Cancel) return;
                omitLiveInputs = choice == MessageBoxResult.Yes;
            }
            plan = ScenePackage.CreatePlan(scene, omitLiveInputs);
        }
        catch (Exception ex)
        {
            ShowPackageMessage($"Failed to prepare the scene for sharing:\n{ex.Message}", "Share Failed", MessageBoxImage.Error);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Share Scene ({plan.Media.Count} media file(s), {ScenePackage.FormatBytes(plan.TotalBytes)})",
            Filter = ScenePackage.DialogFilter + "|All Files|*.*",
            DefaultExt = ScenePackage.Extension,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        string destination = dialog.FileName;
        object originalContent = ShareSceneButton.Content;
        _scenePackageBusy = true;
        ShareSceneButton.IsEnabled = false;
        var progress = new Progress<double>(value => ShareSceneButton.Content = $"Packaging {value:P0}");
        try
        {
            await Task.Run(() => ScenePackage.Write(plan, destination, progress));
        }
        catch (Exception ex)
        {
            ShowPackageMessage($"Failed to write the scene package:\n{ex.Message}", "Share Failed", MessageBoxImage.Error);
            return;
        }
        finally
        {
            _scenePackageBusy = false;
            ShareSceneButton.Content = originalContent;
            ShareSceneButton.IsEnabled = true;
        }

        string notes = plan.NotIncluded.Count == 0 ? "" :
            "\n\nNot included:\n" + string.Join("\n", plan.NotIncluded.Take(12).Select(item => "• " + item)) +
            (plan.NotIncluded.Count > 12 ? $"\n• …and {plan.NotIncluded.Count - 12} more" : "");
        if (ShowPackageMessage(
                $"Saved {Path.GetFileName(destination)} ({plan.Media.Count} media file(s), {ScenePackage.FormatBytes(new FileInfo(destination).Length)}).\n\n" +
                "Send this one file. The other person opens it with Scene Editor → Load... in LifeViz." + notes +
                "\n\nShow it in File Explorer?",
                "Scene Shared", MessageBoxImage.Information, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try { Process.Start("explorer.exe", $"/select,\"{destination}\""); }
            catch (Exception ex) { Logger.Warn($"Could not open File Explorer: {ex.Message}"); }
        }
    }

    private async void ImportScenePackage(string packagePath)
    {
        if (_ownerIsShuttingDown || _scenePackageBusy) return;
        object originalContent = LoadLayersButton.Content;
        _scenePackageBusy = true;
        LoadLayersButton.IsEnabled = false;
        var progress = new Progress<double>(value => LoadLayersButton.Content = $"Unpacking {value:P0}");
        ScenePackage.ImportResult result;
        try
        {
            result = await Task.Run(() => ScenePackage.Import(packagePath, progress: progress));
        }
        catch (Exception ex)
        {
            ShowPackageMessage($"Failed to open the scene package:\n{ex.Message}", "Load Failed", MessageBoxImage.Error);
            return;
        }
        finally
        {
            _scenePackageBusy = false;
            LoadLayersButton.Content = originalContent;
            LoadLayersButton.IsEnabled = true;
        }

        if (_ownerIsShuttingDown) return;
        try
        {
            ApplyLoadedScene(result.Scene);
            Logger.Info($"Imported scene package {packagePath} into {result.MediaFolder}.");
        }
        catch (Exception ex)
        {
            ShowPackageMessage($"Failed to load the shared scene:\n{ex.Message}", "Load Failed", MessageBoxImage.Error);
            return;
        }

        if (result.NotIncluded.Count > 0)
            ShowPackageMessage("The sender's scene also used things that aren't in the package:\n\n" +
                string.Join("\n", result.NotIncluded.Take(12).Select(item => "• " + item)) +
                (result.NotIncluded.Count > 12 ? $"\n• …and {result.NotIncluded.Count - 12} more" : ""),
                "Shared Scene", MessageBoxImage.Information);
    }

    // The editor can close while a package is written in the background.
    private MessageBoxResult ShowPackageMessage(string message, string title, MessageBoxImage image, MessageBoxButton buttons = MessageBoxButton.OK)
    {
        if (App.SuppressErrorDialogs || App.IsSmokeTestMode)
        {
            Logger.Info($"{title}: {message}");
            return MessageBoxResult.None;
        }
        return IsVisible
            ? MessageBox.Show(this, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}
