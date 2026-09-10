using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace lifeviz;

public partial class LayerEditorWindow
{
    private bool _updatingAutoClipSelection;

    private void AutoClipFileList_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(RefreshAutoClipFileSelection));

    private void AutoClipFileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingAutoClipSelection) RefreshAutoClipFileSelection();
    }

    private void RefreshAutoClipFileSelection()
    {
        if (AutoClipFileList == null || AutoClipFileSelectionSummary == null) return;
        var source = AutoClipFileList.DataContext as LayerEditorSource;
        int count = AutoClipFileList.SelectedItems.Count;
        if (source != null)
            source.SelectedAutoClipVideoOverride = count == 1 ? AutoClipFileList.SelectedItems[0] as LayerEditorAutoClipVideoOverride : null;
        AutoClipFileSelectionSummary.Text = $"{count} of {AutoClipFileList.Items.Count} files selected";
        AutoClipRemoveFilesButton.IsEnabled = count > 0;
        AutoClipMoveFilesUpButton.IsEnabled = AutoClipMoveFilesDownButton.IsEnabled = count > 0 && count < AutoClipFileList.Items.Count;
        AutoClipMoveFilesTopButton.IsEnabled = AutoClipMoveFilesBottomButton.IsEnabled = count > 0 && count < AutoClipFileList.Items.Count;
        AutoClipFileOverrides.Visibility = count == 1 ? Visibility.Visible : Visibility.Collapsed;
        AutoClipSingleFileHint.Visibility = count == 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AutoClipSelectAll_Click(object sender, RoutedEventArgs e) => AutoClipFileList.SelectAll();

    private void AutoClipFileList_KeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.A)
        {
            AutoClipFileList.SelectAll();
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Delete)
        {
            RemoveSelectedAutoClipFiles();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            MoveAutoClipVideo(key switch { Key.Up => -1, Key.Down => 1, Key.Home => -2, _ => 2 });
        }
        else return;
        e.Handled = true;
    }

    private HashSet<LayerEditorAutoClipVideoOverride> GetSelectedAutoClipFiles() =>
        AutoClipFileList.SelectedItems.Cast<LayerEditorAutoClipVideoOverride>().ToHashSet();

    private void RestoreAutoClipSelection(IEnumerable<LayerEditorAutoClipVideoOverride> selected)
    {
        AutoClipFileList.SelectedItems.Clear();
        foreach (var item in selected) AutoClipFileList.SelectedItems.Add(item);
        RefreshAutoClipFileSelection();
    }

    // Up/down move each selected run by one place; top/bottom gather the
    // selection while preserving relative order on both sides of the move.
    private void MoveAutoClipVideo(int direction)
    {
        if (AutoClipFileList.DataContext is not LayerEditorSource { IsAutoClip: true } source) return;
        var selected = GetSelectedAutoClipFiles();
        if (selected.Count == 0) return;
        var ordered = source.AutoClipVideoOverrides.ToList();
        if (Math.Abs(direction) == 2)
        {
            ordered = direction < 0
                ? ordered.Where(selected.Contains).Concat(ordered.Where(item => !selected.Contains(item))).ToList()
                : ordered.Where(item => !selected.Contains(item)).Concat(ordered.Where(selected.Contains)).ToList();
        }
        else if (direction < 0)
        {
            for (int i = 1; i < ordered.Count; i++)
                if (selected.Contains(ordered[i]) && !selected.Contains(ordered[i - 1]))
                    (ordered[i - 1], ordered[i]) = (ordered[i], ordered[i - 1]);
        }
        else
        {
            for (int i = ordered.Count - 2; i >= 0; i--)
                if (selected.Contains(ordered[i]) && !selected.Contains(ordered[i + 1]))
                    (ordered[i + 1], ordered[i]) = (ordered[i], ordered[i + 1]);
        }
        if (ordered.SequenceEqual(source.AutoClipVideoOverrides)) return;

        _updatingAutoClipSelection = true;
        try
        {
            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(source.AutoClipVideoOverrides[i], ordered[i]))
                    source.AutoClipVideoOverrides.Move(source.AutoClipVideoOverrides.IndexOf(ordered[i]), i);
            RestoreAutoClipSelection(ordered.Where(selected.Contains));
        }
        finally { _updatingAutoClipSelection = false; }
        CommitAutoClipFileList(source);
        AutoClipFileList.ScrollIntoView(direction < 0 ? ordered.First(selected.Contains) : ordered.Last(selected.Contains));
    }

    private void RemoveSelectedAutoClipFiles()
    {
        if (AutoClipFileList.DataContext is not LayerEditorSource { IsAutoClip: true } source) return;
        var selected = GetSelectedAutoClipFiles();
        if (selected.Count == 0) return;
        int firstIndex = source.AutoClipVideoOverrides.Select((item, index) => (item, index)).First(pair => selected.Contains(pair.item)).index;
        _updatingAutoClipSelection = true;
        try
        {
            foreach (var item in selected) source.AutoClipVideoOverrides.Remove(item);
            var neighbor = source.AutoClipVideoOverrides.ElementAtOrDefault(Math.Min(firstIndex, source.AutoClipVideoOverrides.Count - 1));
            RestoreAutoClipSelection(neighbor == null ? Array.Empty<LayerEditorAutoClipVideoOverride>() : new[] { neighbor });
        }
        finally { _updatingAutoClipSelection = false; }
        CommitAutoClipFileList(source);
    }

    private void CommitAutoClipFileList(LayerEditorSource source)
    {
        source.AutoClipVideoPaths.Clear();
        foreach (var item in source.AutoClipVideoOverrides) source.AutoClipVideoPaths.Add(item.FilePath);
        SyncAutoClipFilePaths(source);
        ApplyAutoClipChange(source);
        RefreshAutoClipFileSelection();
    }
}
