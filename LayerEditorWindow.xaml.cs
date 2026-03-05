using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace lifeviz;

public partial class LayerEditorWindow : Window
{
    private readonly MainWindow _owner;
    private readonly LayerEditorViewModel _viewModel;
    private bool _suppressLiveUpdates;
    private bool _updatingSelection;
    private bool _updatingVideoTransportUi;
    private Point _dragStartPoint;
    private LayerEditorSource? _draggedSource;
    private readonly DispatcherTimer _videoTransportTimer;
    private static readonly JsonSerializerOptions LayerConfigJsonOptions = new() { WriteIndented = true };

    public LayerEditorWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        _viewModel = new LayerEditorViewModel();
        DataContext = _viewModel;
        RefreshMasterAudioState();
        RefreshFromSources();
        UpdateApplyState();
        _videoTransportTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _videoTransportTimer.Tick += (_, _) => RefreshSelectedVideoTransportState();
        _videoTransportTimer.Start();
        Closed += (_, _) => _videoTransportTimer.Stop();
    }

    public void RefreshFromSourcesIfLive()
    {
        if (!_viewModel.LiveMode)
        {
            return;
        }

        RefreshFromSources();
    }

    private void RefreshFromSources(Guid? preferredSelectionId = null)
    {
        var expandedIds = CollectExpandedIds(_viewModel.Sources);
        Guid? selectedId = preferredSelectionId ?? _viewModel.SelectedSource?.Id;

        _suppressLiveUpdates = true;
        try
        {
            var sources = _owner.BuildLayerEditorSources();
            ApplyExpandedState(sources, expandedIds);
            _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);

            LayerEditorSource? selected = null;
            if (selectedId.HasValue)
            {
                selected = FindSourceById(_viewModel.Sources, selectedId.Value);
            }

            selected ??= _viewModel.Sources.FirstOrDefault();
            SetSelectedSource(selected);
            RefreshMasterAudioState();
            RefreshSimulationLayerState();
            RefreshSelectedVideoTransportState();
        }
        finally
        {
            _suppressLiveUpdates = false;
        }
    }

    private void RefreshMasterAudioState()
    {
        _viewModel.SourceAudioMasterEnabled = _owner.GetSourceAudioMasterEnabled();
        _viewModel.SourceAudioMasterVolume = _owner.GetSourceAudioMasterVolume();
    }

    private void RefreshSimulationLayerState()
    {
        _owner.GetSimulationLayerSettingsForEditor(out var simulationLayers);
        _viewModel.SimulationLayers = new ObservableCollection<LayerEditorSimulationLayer>(
            simulationLayers.Select(CloneSimulationLayer));
        SetSelectedSimulationLayer(_viewModel.SimulationLayers.FirstOrDefault());
    }

    private static HashSet<Guid> CollectExpandedIds(IEnumerable<LayerEditorSource> roots)
    {
        var ids = new HashSet<Guid>();
        foreach (var source in EnumerateSources(roots))
        {
            if (source.IsExpanded)
            {
                ids.Add(source.Id);
            }
        }

        return ids;
    }

    private static void ApplyExpandedState(IEnumerable<LayerEditorSource> roots, ISet<Guid> expandedIds)
    {
        foreach (var source in EnumerateSources(roots))
        {
            source.IsExpanded = expandedIds.Contains(source.Id);
        }
    }

    private static IEnumerable<LayerEditorSource> EnumerateSources(IEnumerable<LayerEditorSource> roots)
    {
        foreach (var source in roots)
        {
            yield return source;
            foreach (var child in EnumerateSources(source.Children))
            {
                yield return child;
            }
        }
    }

    private static LayerEditorSource? FindSourceById(IEnumerable<LayerEditorSource> roots, Guid id) =>
        EnumerateSources(roots).FirstOrDefault(source => source.Id == id);

    private void SetSelectedSource(LayerEditorSource? source)
    {
        if (ReferenceEquals(_viewModel.SelectedSource, source))
        {
            return;
        }

        _updatingSelection = true;
        try
        {
            if (_viewModel.SelectedSource != null)
            {
                _viewModel.SelectedSource.IsSelected = false;
            }

            _viewModel.SelectedSource = source;

            if (_viewModel.SelectedSource != null)
            {
                _viewModel.SelectedSource.IsSelected = true;
            }
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private static LayerEditorSimulationLayer CloneSimulationLayer(LayerEditorSimulationLayer source)
    {
        return new LayerEditorSimulationLayer
        {
            Id = source.Id,
            Name = source.Name,
            Enabled = source.Enabled,
            InputFunction = source.InputFunction,
            BlendMode = source.BlendMode
        };
    }

    private void SetSelectedSimulationLayer(LayerEditorSimulationLayer? layer)
    {
        if (ReferenceEquals(_viewModel.SelectedSimulationLayer, layer))
        {
            return;
        }

        if (_viewModel.SelectedSimulationLayer != null)
        {
            _viewModel.SelectedSimulationLayer.IsSelected = false;
        }

        _viewModel.SelectedSimulationLayer = layer;
        if (_viewModel.SelectedSimulationLayer != null)
        {
            _viewModel.SelectedSimulationLayer.IsSelected = true;
        }
    }

    private void UpdateApplyState()
    {
        if (ApplyButton != null)
        {
            ApplyButton.IsEnabled = !_viewModel.LiveMode;
        }
    }

    private bool ShouldApplyLive() => _viewModel.LiveMode && !_suppressLiveUpdates;

    private bool EnsureLiveModeForVideoTransport()
    {
        if (_viewModel.LiveMode)
        {
            return true;
        }

        MessageBox.Show(this, "Enable Live Mode to control video playback.", "Live Mode Required",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void RefreshSelectedVideoTransportState()
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        var source = _viewModel.SelectedSource;
        if (source == null || !source.IsVideo)
        {
            return;
        }

        if (!_owner.TryGetSourceVideoPlaybackState(source.Id, out var playbackState))
        {
            return;
        }

        _updatingVideoTransportUi = true;
        try
        {
            source.VideoPlaybackPaused = playbackState.IsPaused;
            source.VideoPlaybackPosition = playbackState.NormalizedPosition;
            source.VideoPlaybackPositionSeconds = playbackState.PositionSeconds;
            source.VideoPlaybackDurationSeconds = playbackState.DurationSeconds;
        }
        finally
        {
            _updatingVideoTransportUi = false;
        }
    }

    private void LiveModeToggle(object sender, RoutedEventArgs e)
    {
        UpdateApplyState();
        if (_viewModel.LiveMode)
        {
            RefreshFromSources();
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LiveMode)
        {
            return;
        }

        var selectedId = _viewModel.SelectedSource?.Id;
        _owner.ApplyLayerEditorSources(_viewModel.Sources.ToList());
        _owner.ApplySimulationLayerSettingsFromEditor(_viewModel.SimulationLayers.ToList());
        RefreshFromSources(selectedId);
    }

    private void OpenAppControls_Click(object sender, RoutedEventArgs e)
    {
        Point anchor = AppControlsButton.PointToScreen(new Point(0, AppControlsButton.ActualHeight + 2));
        _owner.OpenRootContextMenuAtScreenPoint(anchor.X, anchor.Y);
    }

    private void SaveLayerConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Layer Configuration",
            Filter = "LifeViz Layer Config (*.lifevizlayers.json)|*.lifevizlayers.json|JSON Files|*.json|All Files|*.*",
            DefaultExt = ".lifevizlayers.json",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var config = LayerConfigFile.FromEditorSources(
                _viewModel.Sources,
                _viewModel.SimulationLayers);
            string json = JsonSerializer.Serialize(config, LayerConfigJsonOptions);
            File.WriteAllText(dialog.FileName, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save layer configuration:\n{ex.Message}", "Save Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadLayerConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Layer Configuration",
            Filter = "LifeViz Layer Config (*.lifevizlayers.json)|*.lifevizlayers.json|JSON Files|*.json|All Files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var config = JsonSerializer.Deserialize<LayerConfigFile>(json);
            if (config == null)
            {
                MessageBox.Show(this, "That file did not contain a layer configuration.", "Load Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sources = config.ToEditorSources();
            var simulationLayers = config.ToEditorSimulationLayers();
            if (_viewModel.LiveMode)
            {
                _owner.ApplyLayerEditorSources(sources);
                _owner.ApplySimulationLayerSettingsFromEditor(simulationLayers);
                RefreshFromSources();
            }
            else
            {
                _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);
                SetSelectedSource(_viewModel.Sources.FirstOrDefault());
                _viewModel.SimulationLayers = new ObservableCollection<LayerEditorSimulationLayer>(
                    simulationLayers.Select(CloneSimulationLayer));
                SetSelectedSimulationLayer(_viewModel.SimulationLayers.FirstOrDefault());
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to load layer configuration:\n{ex.Message}", "Load Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private LayerEditorSource? ResolveSourceContext(object sender) =>
        sender is FrameworkElement { DataContext: LayerEditorSource source } ? source : _viewModel.SelectedSource;

    private void BlendMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceBlendMode(source.Id, source.BlendMode);
        }
    }

    private void FitMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceFitMode(source.Id, source.FitMode);
        }
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceOpacity(source.Id, source.Opacity);
        }
    }

    private void Mirror_Changed(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceMirror(source.Id, source.Mirror);
        }
    }

    private void VideoAudio_Changed(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceVideoAudioEnabled(source.Id, source.VideoAudioEnabled);
        }
    }

    private void VideoAudioVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceVideoAudioVolume(source.Id, source.VideoAudioVolume);
        }
    }

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLiveModeForVideoTransport())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source == null || !source.IsVideo)
        {
            return;
        }

        bool pause = !source.VideoPlaybackPaused;
        _owner.UpdateSourceVideoPlaybackPaused(source.Id, pause);
        RefreshSelectedVideoTransportState();
    }

    private void VideoSeek_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!EnsureLiveModeForVideoTransport() || _updatingVideoTransportUi)
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source == null || !source.IsVideo)
        {
            return;
        }

        _owner.SeekSourceVideo(source.Id, source.VideoPlaybackPosition);
        RefreshSelectedVideoTransportState();
    }

    private void MasterVideoAudio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        _owner.UpdateMasterSourceAudioEnabled(_viewModel.SourceAudioMasterEnabled);
    }

    private void MasterVideoAudioVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        _owner.UpdateMasterSourceAudioVolume(_viewModel.SourceAudioMasterVolume);
    }

    private void SimulationLayerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SetSelectedSimulationLayer(e.NewValue as LayerEditorSimulationLayer);
    }

    private void AddSimulationLayerDirect_Click(object sender, RoutedEventArgs e) => AddSimulationLayer("Direct");

    private void AddSimulationLayerInverse_Click(object sender, RoutedEventArgs e) => AddSimulationLayer("Inverse");

    private void AddSimulationLayer(string inputFunction)
    {
        string baseName = string.Equals(inputFunction, "Inverse", StringComparison.OrdinalIgnoreCase)
            ? "Inverse Layer"
            : "Simulation Layer";
        int suffix = 1;
        string nextName = baseName;
        while (_viewModel.SimulationLayers.Any(layer => string.Equals(layer.Name, nextName, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            nextName = $"{baseName} {suffix}";
        }

        var newLayer = new LayerEditorSimulationLayer
        {
            Id = Guid.NewGuid(),
            Name = nextName,
            Enabled = true,
            InputFunction = string.Equals(inputFunction, "Inverse", StringComparison.OrdinalIgnoreCase) ? "Inverse" : "Direct",
            BlendMode = string.Equals(inputFunction, "Inverse", StringComparison.OrdinalIgnoreCase) ? "Subtractive" : "Additive"
        };

        int insertIndex = _viewModel.SelectedSimulationLayer != null
            ? _viewModel.SimulationLayers.IndexOf(_viewModel.SelectedSimulationLayer) + 1
            : _viewModel.SimulationLayers.Count;
        insertIndex = Math.Clamp(insertIndex, 0, _viewModel.SimulationLayers.Count);
        _viewModel.SimulationLayers.Insert(insertIndex, newLayer);
        SetSelectedSimulationLayer(newLayer);

        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void RemoveSimulationLayer_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedSimulationLayer;
        if (selected == null)
        {
            return;
        }

        if (_viewModel.SimulationLayers.Count <= 1)
        {
            MessageBox.Show(this, "At least one simulation layer is required.", "Simulation Layers",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int index = _viewModel.SimulationLayers.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        _viewModel.SimulationLayers.RemoveAt(index);
        var fallback = index < _viewModel.SimulationLayers.Count
            ? _viewModel.SimulationLayers[index]
            : _viewModel.SimulationLayers.LastOrDefault();
        SetSelectedSimulationLayer(fallback);

        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void MoveSimulationLayerUp_Click(object sender, RoutedEventArgs e) => MoveSimulationLayer(-1);

    private void MoveSimulationLayerDown_Click(object sender, RoutedEventArgs e) => MoveSimulationLayer(1);

    private void MoveSimulationLayer(int delta)
    {
        var selected = _viewModel.SelectedSimulationLayer;
        if (selected == null)
        {
            return;
        }

        int index = _viewModel.SimulationLayers.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, _viewModel.SimulationLayers.Count - 1);
        if (next == index)
        {
            return;
        }

        _viewModel.SimulationLayers.Move(index, next);
        SetSelectedSimulationLayer(selected);
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerName_Changed(object sender, TextChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerInputFunction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerBlendMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void ApplySimulationLayerSettingsLive()
    {
        _owner.ApplySimulationLayerSettingsFromEditor(_viewModel.SimulationLayers.ToList());
    }

    private void KeyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceKeyEnabled(source.Id, source.KeyEnabled);
        }
    }

    private void KeyTolerance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceKeyTolerance(source.Id, source.KeyTolerance);
        }
    }

    private void KeyColorHex_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateSourceKeyColor(source.Id, source.KeyColorHex);
        }
    }

    private void GroupName_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.UpdateGroupName(source.Id, source.DisplayName);
        }
    }

    private void MakePrimary_Click(object sender, RoutedEventArgs e)
    {
        var source = ResolveSourceContext(sender);
        if (source == null)
        {
            return;
        }

        MoveSourceToIndex(source, 0);

        if (_viewModel.LiveMode)
        {
            _owner.MakePrimaryFromEditor(source.Id);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSourceBy(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSourceBy(sender, 1);

    private void MoveSourceBy(object sender, int delta)
    {
        var source = ResolveSourceContext(sender);
        if (source == null)
        {
            return;
        }

        var list = GetParentCollection(source);
        int index = list.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, list.Count - 1);
        if (next == index)
        {
            return;
        }

        list.Move(index, next);

        if (_viewModel.LiveMode)
        {
            _owner.MoveSourceFromEditor(source.Id, delta);
        }
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var source = ResolveSourceContext(sender);
        if (source == null)
        {
            return;
        }

        var parentCollection = GetParentCollection(source);
        int index = parentCollection.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        parentCollection.RemoveAt(index);

        LayerEditorSource? fallback = null;
        if (index < parentCollection.Count)
        {
            fallback = parentCollection[index];
        }
        else if (parentCollection.Count > 0)
        {
            fallback = parentCollection[parentCollection.Count - 1];
        }
        else
        {
            fallback = source.Parent;
        }

        SetSelectedSource(fallback);

        if (_viewModel.LiveMode)
        {
            _owner.RemoveSourceFromEditor(source.Id);
        }
    }

    private void RestartVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.LiveMode)
        {
            MessageBox.Show(this, "Enable Live Mode to restart video playback.", "Live Mode Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = ResolveSourceContext(sender);
        if (source != null)
        {
            _owner.RestartVideoFromEditor(source.Id);
        }
    }

    private void ClearScene_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Sources.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(this, "Remove all scene sources and groups?", "Clear Scene",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _viewModel.Sources.Clear();
        SetSelectedSource(null);

        if (_viewModel.LiveMode)
        {
            _owner.ApplyLayerEditorSources(Array.Empty<LayerEditorSource>());
        }
    }

    private void AddZoom_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "ZoomIn", null, null);

    private void AddTranslate_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Translate", "Translate", null);

    private void AddRotate_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Rotate", null, "Rotate");

    private void AddDvd_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "DvdBounce", null, null);

    private void AddBeatShake_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "BeatShake", null, null);

    private void AddAudioGranular_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "AudioGranular", null, null);

    private void AddFade_Click(object sender, RoutedEventArgs e) => AddAnimation(sender, "Fade", null, null);

    private void AddAnimation(object sender, string type, string? translateKey, string? rotateKey)
    {
        var source = ResolveSourceContext(sender);
        if (source == null)
        {
            return;
        }

        if (_viewModel.LiveMode)
        {
            string? translateDirection = translateKey != null ? source.PendingTranslateDirection : null;
            string? rotationDirection = rotateKey != null ? source.PendingRotationDirection : null;
            _owner.AddAnimationFromEditor(source.Id, type, translateDirection, rotationDirection);
            RefreshFromSources(source.Id);
            return;
        }

        var animation = new LayerEditorAnimation
        {
            Id = Guid.NewGuid(),
            Type = type,
            Loop = type == "DvdBounce" ? "PingPong" : "Forward",
            Speed = "Normal",
            TranslateDirection = source.PendingTranslateDirection,
            RotationDirection = source.PendingRotationDirection,
            Parent = source
        };

        source.Animations.Add(animation);
    }

    private void RemoveAnimation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerEditorAnimation animation })
        {
            return;
        }

        var parent = animation.Parent;
        if (parent == null)
        {
            return;
        }

        parent.Animations.Remove(animation);

        if (_viewModel.LiveMode)
        {
            _owner.RemoveAnimationFromEditor(parent.Id, animation.Id);
        }
    }

    private void AnimationLoop_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationSpeed_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationCycle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationTranslateDirection_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationRotateDirection_Changed(object sender, SelectionChangedEventArgs e) => ApplyAnimationChange(sender);

    private void AnimationRotateDegrees_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void AnimationBeatShakeIntensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);
    private void AnimationAudioGranularLowEq_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);
    private void AnimationAudioGranularMidEq_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);
    private void AnimationAudioGranularHighEq_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAnimationChange(sender);

    private void ApplyAnimationChange(object sender)
    {
        if (!ShouldApplyLive())
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: LayerEditorAnimation animation })
        {
            return;
        }

        var parent = animation.Parent;
        if (parent == null)
        {
            return;
        }

        _owner.UpdateAnimationFromEditor(parent.Id, animation);
    }

    private void AddRootGroup_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Group);

    private void AddRootWindow_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Window);

    private void AddRootWebcam_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Webcam);

    private void AddRootFile_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.File);

    private void AddRootYoutube_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.Youtube);

    private void AddRootSequence_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.VideoSequence);

    private void AddChildGroup_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.Group);
        }
    }

    private void AddChildWindow_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.Window);
        }
    }

    private void AddChildWebcam_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.Webcam);
        }
    }

    private void AddChildFile_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.File);
        }
    }

    private void AddChildYoutube_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.Youtube);
        }
    }

    private void AddChildSequence_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.VideoSequence);
        }
    }

    private LayerEditorSource? ResolveSelectedGroup(object sender)
    {
        var source = ResolveSourceContext(sender);
        if (source?.IsGroup == true)
        {
            return source;
        }

        MessageBox.Show(this, "Select a layer group in the Scene Tree first.", "Scene Editor",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void AddSource(LayerEditorSource? parent, LayerEditorSourceKind kind)
    {
        if (_viewModel.LiveMode)
        {
            Guid? parentId = parent?.Id;
            if (AddSourceLive(parent, kind))
            {
                RefreshFromSources(parentId);
            }
            return;
        }

        var draft = BuildDraftSource(kind);
        if (draft == null)
        {
            return;
        }

        draft.Parent = parent;
        GetChildCollection(parent).Add(draft);
        SetSelectedSource(draft);
    }

    private bool AddSourceLive(LayerEditorSource? parent, LayerEditorSourceKind kind)
    {
        Guid? parentId = parent?.Id;
        switch (kind)
        {
            case LayerEditorSourceKind.Group:
                _owner.AddLayerGroupFromEditor(parentId);
                return true;

            case LayerEditorSourceKind.Window:
            {
                var window = PromptForWindow();
                if (window == null)
                {
                    return false;
                }

                _owner.AddWindowSourceFromEditor(window, parentId);
                return true;
            }

            case LayerEditorSourceKind.Webcam:
            {
                var webcam = PromptForWebcam();
                if (!webcam.HasValue)
                {
                    return false;
                }

                _owner.AddWebcamSourceFromEditor(webcam.Value, parentId);
                return true;
            }

            case LayerEditorSourceKind.File:
            {
                var path = PromptForFile();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                _owner.AddFileSourceFromEditor(path, parentId);
                return true;
            }

            case LayerEditorSourceKind.Youtube:
            {
                var url = PromptForYoutube();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return false;
                }

                _owner.AddYoutubeSourceFromEditor(url, parentId);
                return true;
            }

            case LayerEditorSourceKind.VideoSequence:
            {
                var paths = PromptForSequence();
                if (paths == null || paths.Length == 0)
                {
                    return false;
                }

                _owner.AddVideoSequenceFromEditor(paths, parentId);
                return true;
            }
        }

        return false;
    }

    private LayerEditorSource? BuildDraftSource(LayerEditorSourceKind kind)
    {
        switch (kind)
        {
            case LayerEditorSourceKind.Group:
                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Group,
                    DisplayName = "Layer Group",
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };

            case LayerEditorSourceKind.Window:
            {
                var window = PromptForWindow();
                if (window == null)
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Window,
                    DisplayName = window.Title,
                    WindowTitle = window.Title,
                    WindowHandle = window.Handle,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }

            case LayerEditorSourceKind.Webcam:
            {
                var camera = PromptForWebcam();
                if (!camera.HasValue)
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Webcam,
                    DisplayName = camera.Value.Name,
                    WebcamId = camera.Value.Id,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }

            case LayerEditorSourceKind.File:
            {
                var path = PromptForFile();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.File,
                    DisplayName = Path.GetFileName(path),
                    FilePath = path,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }

            case LayerEditorSourceKind.Youtube:
            {
                var url = PromptForYoutube();
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.Youtube,
                    DisplayName = "YouTube Video",
                    FilePath = "youtube:" + url,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }

            case LayerEditorSourceKind.VideoSequence:
            {
                var paths = PromptForSequence();
                if (paths == null || paths.Length == 0)
                {
                    return null;
                }

                var source = new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.VideoSequence,
                    DisplayName = $"Sequence ({paths.Length})",
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
                source.FilePaths.AddRange(paths);
                return source;
            }
        }

        return null;
    }

    private ObservableCollection<LayerEditorSource> GetChildCollection(LayerEditorSource? parent) =>
        parent?.Children ?? _viewModel.Sources;

    private ObservableCollection<LayerEditorSource> GetParentCollection(LayerEditorSource source) =>
        source.Parent?.Children ?? _viewModel.Sources;

    private void MoveSourceToIndex(LayerEditorSource source, int index)
    {
        var list = GetParentCollection(source);
        int currentIndex = list.IndexOf(source);
        if (currentIndex < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, list.Count - 1);
        if (currentIndex == index)
        {
            return;
        }

        list.Move(currentIndex, index);
    }

    private void SceneTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_updatingSelection)
        {
            return;
        }

        if (e.NewValue is LayerEditorSource source)
        {
            SetSelectedSource(source);
        }
        else
        {
            SetSelectedSource(null);
        }
    }

    private void SceneTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(SceneTree);
        _draggedSource = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as LayerEditorSource;
    }

    private void SceneTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedSource == null)
        {
            return;
        }

        var current = e.GetPosition(SceneTree);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(SceneTree, _draggedSource, DragDropEffects.Move);
        _draggedSource = null;
    }

    private void SceneTree_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayerEditorSource)) is not LayerEditorSource dragged)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = TryGetDropTarget(e, dragged, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SceneTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LayerEditorSource)) is not LayerEditorSource dragged)
        {
            return;
        }

        if (!TryGetDropTarget(e, dragged, out var newParent, out var insertIndex))
        {
            return;
        }

        if (!MoveSource(dragged, newParent, insertIndex))
        {
            return;
        }

        SetSelectedSource(dragged);

        if (_viewModel.LiveMode)
        {
            _owner.ApplyLayerEditorSources(_viewModel.Sources.ToList());
        }
    }

    private bool TryGetDropTarget(DragEventArgs e, LayerEditorSource dragged, out LayerEditorSource? newParent, out int insertIndex)
    {
        var targetItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not LayerEditorSource target)
        {
            newParent = null;
            insertIndex = _viewModel.Sources.Count;
            return true;
        }

        if (ReferenceEquals(target, dragged) || IsInSubtree(target, dragged))
        {
            newParent = null;
            insertIndex = -1;
            return false;
        }

        Point position = e.GetPosition(targetItem);
        bool dropAsChild = target.IsGroup && position.X > 28;

        if (dropAsChild)
        {
            newParent = target;
            insertIndex = target.Children.Count;
            return true;
        }

        newParent = target.Parent;
        var siblings = GetChildCollection(newParent);
        int targetIndex = siblings.IndexOf(target);
        if (targetIndex < 0)
        {
            insertIndex = -1;
            return false;
        }

        insertIndex = position.Y > targetItem.ActualHeight / 2 ? targetIndex + 1 : targetIndex;
        return true;
    }

    private static bool IsInSubtree(LayerEditorSource node, LayerEditorSource potentialAncestor)
    {
        var current = node;
        while (current != null)
        {
            if (ReferenceEquals(current, potentialAncestor))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private bool MoveSource(LayerEditorSource source, LayerEditorSource? newParent, int index)
    {
        var oldCollection = GetParentCollection(source);
        int oldIndex = oldCollection.IndexOf(source);
        if (oldIndex < 0)
        {
            return false;
        }

        var newCollection = GetChildCollection(newParent);

        if (ReferenceEquals(oldCollection, newCollection) && index > oldIndex)
        {
            index--;
        }

        index = Math.Clamp(index, 0, newCollection.Count);

        if (ReferenceEquals(oldCollection, newCollection) && oldIndex == index)
        {
            return false;
        }

        oldCollection.RemoveAt(oldIndex);
        source.Parent = newParent;
        newCollection.Insert(index, source);
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private WindowHandleInfo? PromptForWindow()
    {
        var windows = _owner.GetAvailableWindows();
        if (windows.Count == 0)
        {
            MessageBox.Show(this, "No windows detected.", "Window Sources", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var items = windows.Select(window => new SelectionItem(window.Title, window)).ToList();
        var dialog = new SelectionDialog("Select Window Source", items) { Owner = this };
        return dialog.ShowDialog() == true && dialog.SelectedValue is WindowHandleInfo selected ? selected : null;
    }

    private WebcamCaptureService.CameraInfo? PromptForWebcam()
    {
        var webcams = _owner.GetAvailableWebcams();
        if (webcams.Count == 0)
        {
            MessageBox.Show(this, "No webcams detected.", "Webcam Sources", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var items = webcams.Select(camera => new SelectionItem(camera.Name, camera)).ToList();
        var dialog = new SelectionDialog("Select Webcam Source", items) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedValue is WebcamCaptureService.CameraInfo selected)
        {
            return selected;
        }

        return null;
    }

    private string? PromptForFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Image, GIF, or Video",
            Filter = "Media Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string[]? PromptForSequence()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Video Sequence",
            Filter = "Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    private string? PromptForYoutube()
    {
        var dialog = new TextInputDialog("Add YouTube Source", "Enter YouTube URL:") { Owner = this };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText) ? dialog.InputText.Trim() : null;
    }
}
