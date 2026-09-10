using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class MainWindow
{
    // Visibility groups are routing labels, not flattened compositing groups.
    // Resolve within one sibling stack after capture, before either renderer runs.
    private static void UpdateVisibilityGroups(List<CaptureSource> sources)
    {
        foreach (var source in sources)
        {
            source.VisibilityOpacity = 1;
            if (source.Type == CaptureSource.SourceType.SimGroup ||
                (source.Type == CaptureSource.SourceType.AutoClip && source.AutoClipTakeover) ||
                string.IsNullOrWhiteSpace(source.VisibilityGroup))
            {
                continue;
            }

            double takeover = 0;
            foreach (var candidate in sources)
            {
                if (candidate.Type != CaptureSource.SourceType.AutoClip || !candidate.AutoClipTakeover ||
                    !candidate.Enabled || candidate.HasError || candidate.LastFrame == null || candidate.Opacity <= 0 ||
                    !string.Equals(source.VisibilityGroup, candidate.VisibilityGroup, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                takeover = Math.Max(takeover, candidate.AutoClip?.GetVisualOpacity(candidate.AutoClipFadeSeconds) ?? 0);
            }

            source.VisibilityOpacity = 1 - takeover;
        }
    }

    internal void UpdateSourceVisibilityFromEditor(Guid sourceId, string? group, bool takeover)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null) return;
            source.VisibilityGroup = (group ?? string.Empty).Trim();
            source.AutoClipTakeover = source.Type == CaptureSource.SourceType.AutoClip && takeover;
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateAutoClipPlaybackOptionsFromEditor(Guid sourceId, bool startWithDelay, bool inOrder, bool wholeFile)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source?.Type != CaptureSource.SourceType.AutoClip) return;
            source.AutoClipStartWithDelay = startWithDelay;
            source.AutoClipPlayInOrder = inOrder;
            source.AutoClipPlayWholeFile = wholeFile;
            ApplyAutoClipPlaybackOptions(source);
            source.LastFrame = null;
            RenderFrame();
            SaveConfig();
        });
    }

    private static void ApplyAutoClipPlaybackOptions(CaptureSource source) =>
        source.AutoClip?.SetPlaybackOptions(source.AutoClipStartWithDelay, source.AutoClipPlayInOrder, source.AutoClipPlayWholeFile);

    private void AddVisibilityGroupMenu(MenuItem menu, CaptureSource source)
    {
        if (source.Type == CaptureSource.SourceType.SimGroup) return;
        var groupItem = new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(source.VisibilityGroup)
                ? "Visibility Group... (independent)"
                : $"Visibility Group... ({source.VisibilityGroup})"
        };
        groupItem.Click += (_, _) =>
        {
            var dialog = new TextInputDialog("Visibility Group",
                "Use the same name for related layers at this stack level. Leave blank for independent playback.", source.VisibilityGroup) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            UpdateSourceVisibilityFromEditor(source.Id, dialog.InputText, source.AutoClipTakeover);
            RebuildSourcesMenu();
            NotifyLayerEditorSourcesChanged();
        };
        menu.Items.Add(groupItem);
        if (source.Type != CaptureSource.SourceType.AutoClip) return;

        var takeover = new MenuItem { Header = "Take Over Visibility Group", IsCheckable = true, IsChecked = source.AutoClipTakeover };
        takeover.Click += (_, _) =>
        {
            UpdateSourceVisibilityFromEditor(source.Id, source.VisibilityGroup, takeover.IsChecked);
            NotifyLayerEditorSourcesChanged();
        };
        menu.Items.Add(takeover);

        void AddOption(string label, Func<bool> read, Action<bool> write)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = read() };
            item.Click += (_, _) =>
            {
                write(item.IsChecked);
                UpdateAutoClipPlaybackOptionsFromEditor(source.Id, source.AutoClipStartWithDelay, source.AutoClipPlayInOrder, source.AutoClipPlayWholeFile);
                NotifyLayerEditorSourcesChanged();
            };
            menu.Items.Add(item);
        }
        AddOption("Start with Delay", () => source.AutoClipStartWithDelay, value => source.AutoClipStartWithDelay = value);
        AddOption("Play Files in List Order", () => source.AutoClipPlayInOrder, value => source.AutoClipPlayInOrder = value);
        AddOption("Play Whole File", () => source.AutoClipPlayWholeFile, value => source.AutoClipPlayWholeFile = value);
    }
}
