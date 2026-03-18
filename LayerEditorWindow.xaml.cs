using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace lifeviz;

public partial class LayerEditorWindow : Window
{
    private readonly MainWindow _owner;
    private readonly LayerEditorViewModel _viewModel;
    private LayerEditorProjectSettings? _pendingProjectSettings;
    private bool _ownerIsShuttingDown;
    private bool _suppressLiveUpdates;
    private bool _suppressSimulationLayerBindingApply;
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
        if (owner.IsLoaded || owner.IsVisible)
        {
            Owner = owner;
        }
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

    public void PrepareForOwnerShutdown()
    {
        _ownerIsShuttingDown = true;
        _suppressLiveUpdates = true;
        _pendingProjectSettings = null;
        _videoTransportTimer.Stop();
    }

    public void RefreshFromSourcesIfLive()
    {
        if (_ownerIsShuttingDown || !_viewModel.LiveMode)
        {
            return;
        }

        RefreshFromSources();
    }

    internal void SetLiveModeForSmoke(bool enabled)
    {
        LiveModeCheckBox.IsChecked = enabled;
    }

    internal void ApplySimulationHeightForSmoke(int height, bool applyImmediately)
    {
        int normalizedHeight = NormalizeSimulationHeight(height);
        SimulationHeightComboBox.SelectedItem = normalizedHeight;
        SimulationHeight_DropDownClosed(SimulationHeightComboBox, EventArgs.Empty);

        if (applyImmediately)
        {
            ApplyButton_Click(ApplyButton, new RoutedEventArgs(Button.ClickEvent, ApplyButton));
        }
    }

    internal bool RunSimulationLayerReactiveIsolationSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        var first = simulationSource?.SimulationLayers.FirstOrDefault();
        if (simulationSource == null || first == null)
        {
            return false;
        }

        first.ReactiveMappings.Clear();
        first.ReactiveMappings.Add(new LayerEditorSimulationReactiveMapping
        {
            Id = Guid.NewGuid(),
            Input = nameof(SimulationReactiveInput.Level),
            Output = nameof(SimulationReactiveOutput.Opacity),
            Amount = 1.0
        });
        first.AudioFrequencyHueShiftDegrees = 90;
        SetSelectedSource(simulationSource);
        SetSelectedSimulationLayer(first);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        bool reactiveUiVisible = SelectedSimulationReactiveMappingsGroupBox.IsVisible &&
                                 SelectedSimulationAddReactiveMappingButton.IsVisible &&
                                 SelectedSimulationReactiveMappingsGroupBox.DataContext is LayerEditorSimulationLayer visibleLayer &&
                                 visibleLayer.Id == first.Id;

        AddSimulationLayer(LayerEditorSimulationLayerType.Life);
        var second = GetSelectedSimulationLayer();
        if (second == null || ReferenceEquals(second, first))
        {
            return false;
        }

        bool newLayerDidNotInherit = second.ReactiveMappings.Count == 0 &&
                                     second.AudioFrequencyHueShiftDegrees == 0 &&
                                     first.ReactiveMappings.Count == 1;
        return reactiveUiVisible && newLayerDidNotInherit;
    }

    internal bool RunSimGroupSelectionSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        if (simulationSource == null)
        {
            Logger.Warn("Sim-group selection smoke: no simulation group source found.");
            return false;
        }

        UpdateLayout();
        SceneTree.UpdateLayout();
        var container = FindTreeViewItem(SceneTree, simulationSource);
        if (container == null)
        {
            Logger.Warn($"Sim-group selection smoke: could not find tree container for {DescribeSource(simulationSource)}.");
            return false;
        }

        TraceSelection($"Smoke selecting scene-tree item {DescribeSource(simulationSource)}");
        container.IsSelected = true;
        container.Focus();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        TraceSelection($"Smoke selected source -> {DescribeSource(_viewModel.SelectedSource)}");
        return _viewModel.SelectedSource?.Id == simulationSource.Id;
    }

    internal bool RunSimGroupLiveEditSelectionSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        var selectedLayer = simulationSource?.SimulationLayers.FirstOrDefault(layer => !layer.IsGroup) ?? simulationSource?.SimulationLayers.FirstOrDefault();
        if (simulationSource == null || selectedLayer == null)
        {
            Logger.Warn("Sim-group live-edit selection smoke: missing sim-group source or child layer.");
            return false;
        }

        SetSelectedSource(simulationSource);
        SetSelectedSimulationLayer(selectedLayer);
        TraceSelection($"Smoke pre-edit source={DescribeSource(_viewModel.SelectedSource)} layer={DescribeSimulationLayer(GetSelectedSimulationLayer())}");
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        double nextThreshold = Math.Clamp(selectedLayer.ThresholdMin + 0.01, 0, selectedLayer.ThresholdMax);
        SelectedSimulationThresholdMinSlider.Value = nextThreshold;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        TraceSelection($"Smoke post-edit source={DescribeSource(_viewModel.SelectedSource)} layer={DescribeSimulationLayer(GetSelectedSimulationLayer())}");
        bool runtimeUpdated = _owner.TryGetSimulationLayerThresholdMinForSmoke(selectedLayer.Id, out var runtimeThresholdMin) &&
                              Math.Abs(runtimeThresholdMin - nextThreshold) < 0.0001;
        return _viewModel.SelectedSource?.Id == simulationSource.Id && runtimeUpdated;
    }

    internal bool RunSimGroupEnabledToggleSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        var selectedLayer = simulationSource?.SimulationLayers.FirstOrDefault(layer => !layer.IsGroup);
        if (simulationSource == null || selectedLayer == null)
        {
            Logger.Warn("Sim-group enabled toggle smoke: missing sim-group source or child layer.");
            return false;
        }

        SetSelectedSource(simulationSource);
        SetSelectedSimulationLayer(selectedLayer);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        var before = _owner.GetSimulationLayerCountsForSmoke();
        bool targetState = !selectedLayer.Enabled;
        SelectedSimulationEnabledCheckBox.IsChecked = targetState;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        var after = _owner.GetSimulationLayerCountsForSmoke();
        bool expectedChanged = targetState ? after.enabledLayers == before.enabledLayers + 1 : after.enabledLayers == before.enabledLayers - 1;
        TraceSelection(
            $"Smoke enabled-toggle source={DescribeSource(_viewModel.SelectedSource)} layer={DescribeSimulationLayer(GetSelectedSimulationLayer())} " +
            $"before=({before.totalLayers},{before.enabledLayers}) after=({after.totalLayers},{after.enabledLayers}) targetState={targetState}");
        return _viewModel.SelectedSource?.Id == simulationSource.Id && after.totalLayers == before.totalLayers && expectedChanged;
    }

    internal bool RunSimGroupRemoveSourceSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        if (simulationSource == null)
        {
            Logger.Warn("Sim-group remove-source smoke: missing sim-group source.");
            return false;
        }

        SetSelectedSource(simulationSource);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        SceneRemoveSourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, SceneRemoveSourceButton));
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        bool sourceRemoved = EnumerateSources(_viewModel.Sources).All(source => source.Id != simulationSource.Id);
        var after = _owner.GetSimulationLayerCountsForSmoke();
        TraceSelection(
            $"Smoke remove-source removed={sourceRemoved} runtime=({after.totalLayers},{after.enabledLayers}) selected={DescribeSource(_viewModel.SelectedSource)}");
        return sourceRemoved && after.totalLayers == 0 && after.enabledLayers == 0;
    }

    internal bool RunPixelSortEditorRoundTripSmoke()
    {
        RefreshFromSources();
        var simulationSource = EnsureSimulationSourceForSmoke();
        if (simulationSource == null)
        {
            Logger.Warn("Pixel-sort editor round-trip smoke: missing sim-group source.");
            return false;
        }

        SetSelectedSource(simulationSource);
        AddSimulationLayer(LayerEditorSimulationLayerType.PixelSort);
        var addedLayer = GetSelectedSimulationLayer();
        if (addedLayer == null)
        {
            Logger.Warn("Pixel-sort editor round-trip smoke: add did not select a new layer.");
            return false;
        }

        Guid addedLayerId = addedLayer.Id;
        addedLayer.PixelSortCellWidth = 19;
        addedLayer.PixelSortCellHeight = 11;
        ApplySimulationLayerSettingsLive(force: true);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        bool runtimeBeforeRefreshOk = _owner.TryGetSimulationLayerRuntimeInfoForSmoke(
            addedLayerId,
            out var runtimeTypeBeforeRefresh,
            out var runtimeColumnsBeforeRefresh,
            out var runtimeRowsBeforeRefresh) &&
            string.Equals(runtimeTypeBeforeRefresh, "PixelSort", StringComparison.Ordinal) &&
            runtimeColumnsBeforeRefresh == 19 &&
            runtimeRowsBeforeRefresh == 11;

        RefreshFromSources(simulationSource.Id);
        var refreshedSource = EnumerateSources(_viewModel.Sources).FirstOrDefault(source => source.Id == simulationSource.Id);
        var refreshedLayer = refreshedSource == null ? null : FindSimulationLayerById(refreshedSource.SimulationLayers, addedLayerId);
        bool editorRefreshOk = refreshedLayer?.LayerType == LayerEditorSimulationLayerType.PixelSort &&
                               refreshedLayer.PixelSortCellWidth == 19 &&
                               refreshedLayer.PixelSortCellHeight == 11;

        bool runtimeAfterRefreshOk = _owner.TryGetSimulationLayerRuntimeInfoForSmoke(
            addedLayerId,
            out var runtimeTypeAfterRefresh,
            out var runtimeColumnsAfterRefresh,
            out var runtimeRowsAfterRefresh) &&
            string.Equals(runtimeTypeAfterRefresh, "PixelSort", StringComparison.Ordinal) &&
            runtimeColumnsAfterRefresh == 19 &&
            runtimeRowsAfterRefresh == 11;

        Logger.Info(
            $"Pixel-sort editor round-trip smoke: runtimeBeforeRefresh={runtimeBeforeRefreshOk}, " +
            $"editorRefresh={editorRefreshOk}, runtimeAfterRefresh={runtimeAfterRefreshOk}.");
        return runtimeBeforeRefreshOk && editorRefreshOk && runtimeAfterRefreshOk;
    }

    private LayerEditorSource? EnsureSimulationSourceForSmoke()
    {
        var simulationSource = EnumerateSources(_viewModel.Sources).FirstOrDefault(source => source.IsSimulationGroup);
        if (simulationSource != null)
        {
            return simulationSource;
        }

        if (!_viewModel.Sources.Any())
        {
            _owner.AddLayerGroupFromEditor(null);
            RefreshFromSources();
        }

        _owner.AddSimulationGroupFromEditor(null);
        RefreshFromSources();
        simulationSource = EnumerateSources(_viewModel.Sources).FirstOrDefault(source => source.IsSimulationGroup);
        if (simulationSource != null && simulationSource.SimulationLayers.Count == 0)
        {
            SetSelectedSource(simulationSource);
            AddSimulationLayer(LayerEditorSimulationLayerType.Life);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            simulationSource = EnumerateSources(_viewModel.Sources).FirstOrDefault(source => source.IsSimulationGroup);
        }

        return simulationSource;
    }

    private void RefreshFromSources(Guid? preferredSelectionId = null)
    {
        var expandedIds = CollectExpandedIds(_viewModel.Sources);
        var selectedSimulationLayerIds = CollectSelectedSimulationLayerIds(_viewModel.Sources);
        Guid? selectedId = preferredSelectionId ?? _viewModel.SelectedSource?.Id;

        _suppressLiveUpdates = true;
        try
        {
            var sources = _owner.BuildLayerEditorSources();
            ApplyExpandedState(sources, expandedIds);
            ApplySelectedSimulationLayerState(sources, selectedSimulationLayerIds);
            _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);

            LayerEditorSource? selected = null;
            if (selectedId.HasValue)
            {
                selected = FindSourceById(_viewModel.Sources, selectedId.Value);
                if (selected == null)
                {
                    TraceSelection($"RefreshFromSources could not find preferred source id={selectedId.Value}");
                }
            }

            selected ??= _viewModel.Sources.FirstOrDefault();
            SetSelectedSource(selected);
            TraceSelection($"RefreshFromSources selected={DescribeSource(selected)} preferred={preferredSelectionId}");
            RefreshMasterAudioState();
            RefreshProjectSettingsState();
            RefreshSelectedVideoTransportState();
            _pendingProjectSettings = null;
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

    private void RefreshProjectSettingsState()
    {
        var projectSettings = _pendingProjectSettings ?? _owner.GetProjectSettingsForEditor();
        _viewModel.SimulationHeight = projectSettings.Height;
        _viewModel.SimulationDepth = projectSettings.Depth;
        _viewModel.SimulationFramerate = projectSettings.Framerate;
        _viewModel.GlobalSimulationLifeOpacity = projectSettings.LifeOpacity;
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

    private static Dictionary<Guid, Guid> CollectSelectedSimulationLayerIds(IEnumerable<LayerEditorSource> roots)
    {
        var ids = new Dictionary<Guid, Guid>();
        foreach (var source in EnumerateSources(roots))
        {
            if (source.IsSimulationGroup && source.SelectedSimulationLayer != null)
            {
                ids[source.Id] = source.SelectedSimulationLayer.Id;
            }
        }

        return ids;
    }

    private static void ApplySelectedSimulationLayerState(IEnumerable<LayerEditorSource> roots, IReadOnlyDictionary<Guid, Guid> selectedLayerIds)
    {
        foreach (var source in EnumerateSources(roots))
        {
            if (!source.IsSimulationGroup || !selectedLayerIds.TryGetValue(source.Id, out var selectedLayerId))
            {
                continue;
            }

            var selectedLayer = FindSimulationLayerById(source.SimulationLayers, selectedLayerId);
            if (selectedLayer == null)
            {
                continue;
            }

            source.SelectedSimulationLayer = selectedLayer;
            selectedLayer.IsSelected = true;
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
            TraceSelection($"SetSelectedSource noop {DescribeSource(source)}");
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

            TraceSelection($"SetSelectedSource -> {DescribeSource(_viewModel.SelectedSource)}");
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private static LayerEditorSimulationLayer CloneSimulationLayer(LayerEditorSimulationLayer source)
    {
        var clone = new LayerEditorSimulationLayer
        {
            Id = source.Id,
            Kind = source.Kind,
            LayerType = source.LayerType,
            Name = source.Name,
            Enabled = source.Enabled,
            InputFunction = source.InputFunction,
            BlendMode = source.BlendMode,
            InjectionMode = source.InjectionMode,
            LifeMode = source.LifeMode,
            BinningMode = source.BinningMode,
            InjectionNoise = source.InjectionNoise,
            LifeOpacity = source.LifeOpacity,
            RgbHueShiftDegrees = source.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = source.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = source.AudioFrequencyHueShiftDegrees,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(
                source.ReactiveMappings.Select(CloneReactiveMapping)),
            ThresholdMin = source.ThresholdMin,
            ThresholdMax = source.ThresholdMax,
            InvertThreshold = source.InvertThreshold,
            PixelSortCellWidth = source.PixelSortCellWidth,
            PixelSortCellHeight = source.PixelSortCellHeight
        };

        foreach (var child in source.Children)
        {
            var childClone = CloneSimulationLayer(child);
            childClone.Parent = clone;
            clone.Children.Add(childClone);
        }

        return clone;
    }

    private static LayerEditorSimulationReactiveMapping CloneReactiveMapping(LayerEditorSimulationReactiveMapping source)
    {
        return new LayerEditorSimulationReactiveMapping
        {
            Id = source.Id,
            Input = source.Input,
            Output = source.Output,
            Amount = source.Amount,
            ThresholdMin = source.ThresholdMin,
            ThresholdMax = source.ThresholdMax
        };
    }

    private void AttachReactiveMappingHandlers(LayerEditorSimulationLayer layer)
    {
        foreach (var mapping in layer.ReactiveMappings)
        {
            mapping.PropertyChanged -= ReactiveMapping_PropertyChanged;
            mapping.PropertyChanged += ReactiveMapping_PropertyChanged;
        }

        foreach (var child in layer.Children)
        {
            AttachReactiveMappingHandlers(child);
        }
    }

    private void ReactiveMapping_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        GetSelectedSimulationLayer()?.NotifyDetailsChanged();
    }

    private LayerEditorSource? GetSelectedSimulationSource() =>
        _viewModel.SelectedSource?.IsSimulationGroup == true ? _viewModel.SelectedSource : null;

    private LayerEditorSimulationLayer? GetSelectedSimulationLayer() =>
        GetSelectedSimulationSource()?.SelectedSimulationLayer;

    private ObservableCollection<LayerEditorSimulationLayer>? GetSelectedSimulationLayers() =>
        GetSelectedSimulationSource()?.SimulationLayers;

    private static IEnumerable<LayerEditorSimulationLayer> EnumerateSimulationLayers(IEnumerable<LayerEditorSimulationLayer> roots)
    {
        foreach (var layer in roots)
        {
            yield return layer;
            foreach (var child in EnumerateSimulationLayers(layer.Children))
            {
                yield return child;
            }
        }
    }

    private static LayerEditorSimulationLayer? FindSimulationLayerById(IEnumerable<LayerEditorSimulationLayer> roots, Guid id) =>
        EnumerateSimulationLayers(roots).FirstOrDefault(layer => layer.Id == id);

    private void TraceSelection(string message)
    {
        Logger.Info($"[LayerEditorSelection] {message}");
    }

    private static string DescribeObject(object? value) => value switch
    {
        LayerEditorSource source => DescribeSource(source),
        LayerEditorSimulationLayer layer => DescribeSimulationLayer(layer),
        null => "<null>",
        _ => value.GetType().Name
    };

    private static string DescribeSource(LayerEditorSource? source) =>
        source == null ? "<null>" : $"{source.Kind}:{source.DisplayName} ({source.Id})";

    private static string DescribeSimulationLayer(LayerEditorSimulationLayer? layer) =>
        layer == null ? "<null>" : $"{layer.Kind}:{layer.Name} ({layer.Id})";

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childContainer)
            {
                continue;
            }

            var nested = FindTreeViewItem(childContainer, item);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private void SetSelectedSimulationLayer(LayerEditorSimulationLayer? layer)
    {
        var source = GetSelectedSimulationSource();
        if (source == null || ReferenceEquals(source.SelectedSimulationLayer, layer))
        {
            TraceSelection($"SetSelectedSimulationLayer noop source={DescribeSource(source)} layer={DescribeSimulationLayer(layer)}");
            return;
        }

        if (source.SelectedSimulationLayer != null)
        {
            source.SelectedSimulationLayer.IsSelected = false;
        }

        _suppressSimulationLayerBindingApply = true;
        source.SelectedSimulationLayer = layer;
        if (source.SelectedSimulationLayer != null)
        {
            source.SelectedSimulationLayer.IsSelected = true;
        }

        TraceSelection($"SetSelectedSimulationLayer source={DescribeSource(source)} layer={DescribeSimulationLayer(layer)}");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _suppressSimulationLayerBindingApply = false;
        }));
    }

    private void UpdateApplyState()
    {
        if (ApplyButton != null)
        {
            ApplyButton.IsEnabled = !_viewModel.LiveMode;
        }
    }

    private bool ShouldApplyLive() =>
        !ReferenceEquals(_viewModel, null) &&
        !_ownerIsShuttingDown &&
        !_suppressLiveUpdates &&
        DataContext is LayerEditorViewModel { LiveMode: true };

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

    private LayerEditorProjectSettings BuildProjectSettingsFromViewModel()
    {
        if (_ownerIsShuttingDown)
        {
            return _pendingProjectSettings ?? new LayerEditorProjectSettings();
        }

        var settings = _pendingProjectSettings ?? _owner.GetProjectSettingsForEditor();
        settings.Height = NormalizeSimulationHeight(_viewModel.SimulationHeight);
        settings.Depth = Math.Clamp(_viewModel.SimulationDepth, 3, 96);
        settings.Framerate = Math.Clamp(_viewModel.SimulationFramerate, 5, 144);
        settings.LifeOpacity = Math.Clamp(_viewModel.GlobalSimulationLifeOpacity, 0, 1);
        return settings;
    }

    private void ApplyProjectSettingsLiveIfNeeded()
    {
        var settings = BuildProjectSettingsFromViewModel();
        if (ShouldApplyLive())
        {
            _owner.ApplyProjectSettingsFromEditor(settings);
            _pendingProjectSettings = null;
        }
        else
        {
            _pendingProjectSettings = settings;
        }
    }

    private void CommitSimulationDimensions()
    {
        _viewModel.SimulationHeight = NormalizeSimulationHeight(_viewModel.SimulationHeight);
        _viewModel.SimulationDepth = Math.Clamp(_viewModel.SimulationDepth, 3, 96);
        _viewModel.SimulationFramerate = Math.Clamp(_viewModel.SimulationFramerate, 5, 144);
        ApplyProjectSettingsLiveIfNeeded();
    }

    private void RefreshSelectedVideoTransportState()
    {
        if (_ownerIsShuttingDown || _suppressLiveUpdates)
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
        if (_ownerIsShuttingDown || _viewModel.LiveMode)
        {
            return;
        }

        if (_pendingProjectSettings != null)
        {
            _owner.ApplyProjectSettingsFromEditor(_pendingProjectSettings);
            _pendingProjectSettings = null;
        }

        var selectedId = _viewModel.SelectedSource?.Id;
        _owner.ApplyLayerEditorSources(_viewModel.Sources.ToList());
        RefreshFromSources(selectedId);
    }

    private void OpenAppControls_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerIsShuttingDown)
        {
            return;
        }

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
            var projectSettings = _pendingProjectSettings ?? _owner.GetProjectSettingsForEditor();
            var config = LayerConfigFile.FromEditorSources(
                _viewModel.Sources,
                Array.Empty<LayerEditorSimulationLayer>(),
                projectSettings);
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
            var projectSettings = config.ToEditorProjectSettings();
            if (_viewModel.LiveMode)
            {
                _owner.ApplyProjectSettingsFromEditor(projectSettings);
                _owner.ApplyLayerEditorSources(sources);
                RefreshFromSources();
            }
            else
            {
                _pendingProjectSettings = projectSettings;
                _viewModel.Sources = new ObservableCollection<LayerEditorSource>(sources);
                SetSelectedSource(_viewModel.Sources.FirstOrDefault());
                RefreshProjectSettingsState();
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
        TraceSelection($"SimulationLayerTree_SelectedItemChanged old={DescribeObject(e.OldValue)} new={DescribeObject(e.NewValue)}");
        SetSelectedSimulationLayer(e.NewValue as LayerEditorSimulationLayer);
    }

    private void AddSimulationLayerDirect_Click(object sender, RoutedEventArgs e) => AddSimulationLayer(LayerEditorSimulationLayerType.Life);

    private void AddSimulationPixelSort_Click(object sender, RoutedEventArgs e) => AddSimulationLayer(LayerEditorSimulationLayerType.PixelSort);

    private void AddSimulationGroup_Click(object sender, RoutedEventArgs e) => AddSimulationGroup();

    private void AddSimulationLayer(LayerEditorSimulationLayerType layerType)
    {
        var simulationSource = GetSelectedSimulationSource();
        var simulationLayers = simulationSource?.SimulationLayers;
        if (simulationSource == null || simulationLayers == null)
        {
            return;
        }

        string baseName = layerType == LayerEditorSimulationLayerType.PixelSort
            ? "Pixel Sort"
            : "Life Sim";
        int suffix = 1;
        string nextName = baseName;
        while (EnumerateSimulationLayers(simulationLayers).Any(layer => string.Equals(layer.Name, nextName, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            nextName = $"{baseName} {suffix}";
        }

        var selectedSimulationLayer = GetSelectedSimulationLayer();

        var newLayer = new LayerEditorSimulationLayer
        {
            Id = Guid.NewGuid(),
            LayerType = layerType,
            Name = nextName,
            Enabled = true,
            InputFunction = "Direct",
            BlendMode = selectedSimulationLayer?.BlendMode
                ?? (layerType == LayerEditorSimulationLayerType.PixelSort ? "Normal" : "Additive"),
            InjectionMode = selectedSimulationLayer?.InjectionMode ?? "Threshold",
            LifeMode = selectedSimulationLayer?.LifeMode ?? "NaiveGrayscale",
            BinningMode = selectedSimulationLayer?.BinningMode ?? "Fill",
            InjectionNoise = selectedSimulationLayer?.InjectionNoise ?? 0.0,
            LifeOpacity = selectedSimulationLayer?.LifeOpacity ?? 1.0,
            RgbHueShiftDegrees = selectedSimulationLayer?.RgbHueShiftDegrees ?? 0.0,
            RgbHueShiftSpeedDegreesPerSecond = selectedSimulationLayer?.RgbHueShiftSpeedDegreesPerSecond ?? 0.0,
            AudioFrequencyHueShiftDegrees = 0.0,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(),
            ThresholdMin = selectedSimulationLayer?.ThresholdMin ?? 0.35,
            ThresholdMax = selectedSimulationLayer?.ThresholdMax ?? 0.75,
            InvertThreshold = selectedSimulationLayer?.InvertThreshold ?? false,
            PixelSortCellWidth = selectedSimulationLayer?.PixelSortCellWidth ?? 12,
            PixelSortCellHeight = selectedSimulationLayer?.PixelSortCellHeight ?? 8
        };
        AttachReactiveMappingHandlers(newLayer);

        var (targetCollection, insertIndex, parent) = ResolveSimulationInsertLocation(selectedSimulationLayer, simulationLayers);
        newLayer.Parent = parent;
        insertIndex = Math.Clamp(insertIndex, 0, targetCollection.Count);
        targetCollection.Insert(insertIndex, newLayer);
        SetSelectedSimulationLayer(newLayer);

        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive(force: true);
        }
    }

    private void AddSimulationGroup()
    {
        var simulationLayers = GetSelectedSimulationLayers();
        if (simulationLayers == null)
        {
            return;
        }

        string baseName = "Sim Group";
        int suffix = 1;
        string nextName = baseName;
        while (EnumerateSimulationLayers(simulationLayers).Any(layer => string.Equals(layer.Name, nextName, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            nextName = $"{baseName} {suffix}";
        }

        var group = new LayerEditorSimulationLayer
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSimulationItemKind.Group,
            Name = nextName,
            Enabled = true
        };

        var (targetCollection, insertIndex, parent) = ResolveSimulationInsertLocation(GetSelectedSimulationLayer(), simulationLayers);
        group.Parent = parent;
        insertIndex = Math.Clamp(insertIndex, 0, targetCollection.Count);
        targetCollection.Insert(insertIndex, group);
        SetSelectedSimulationLayer(group);

        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive(force: true);
        }
    }

    private void RemoveSimulationLayer_Click(object sender, RoutedEventArgs e)
    {
        var simulationLayers = GetSelectedSimulationLayers();
        var selected = GetSelectedSimulationLayer();
        if (simulationLayers == null || selected == null)
        {
            return;
        }

        if (EnumerateSimulationLayers(simulationLayers).Count(layer => !layer.IsGroup) <= CountSimulationLeafLayers(selected))
        {
            MessageBox.Show(this, "At least one simulation layer is required.", "Simulation Layers",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var collection = GetSimulationParentCollection(selected, simulationLayers);
        int index = collection.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        collection.RemoveAt(index);
        var fallback = index < collection.Count
            ? collection[index]
            : collection.LastOrDefault() ?? selected.Parent;
        SetSelectedSimulationLayer(fallback);

        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive(force: true);
        }
    }

    private void MoveSimulationLayerUp_Click(object sender, RoutedEventArgs e) => MoveSimulationLayer(-1);

    private void MoveSimulationLayerDown_Click(object sender, RoutedEventArgs e) => MoveSimulationLayer(1);

    private void MoveSimulationLayer(int delta)
    {
        var simulationLayers = GetSelectedSimulationLayers();
        var selected = GetSelectedSimulationLayer();
        if (simulationLayers == null || selected == null)
        {
            return;
        }

        var collection = GetSimulationParentCollection(selected, simulationLayers);
        int index = collection.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, collection.Count - 1);
        if (next == index)
        {
            return;
        }

        collection.Move(index, next);
        SetSelectedSimulationLayer(selected);
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive(force: true);
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

    private void SimulationLayerInjectionMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerInvertThreshold_Changed(object sender, RoutedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void GlobalSimulationLifeOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        ApplyProjectSettingsLiveIfNeeded();
    }

    private void SimulationHeight_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        CommitSimulationDimensionBinding(sender);
        CommitSimulationDimensions();
    }

    private void SimulationHeight_DropDownClosed(object sender, EventArgs e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        CommitSimulationDimensionBinding(sender);
        CommitSimulationDimensions();
    }

    private void SimulationDimensions_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveUpdates)
        {
            return;
        }

        CommitSimulationDimensionBinding(sender);
        CommitSimulationDimensions();
    }

    private void SimulationDimensions_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitSimulationDimensionBinding(sender);
        CommitSimulationDimensions();
    }

    private static void CommitSimulationDimensionBinding(object sender)
    {
        if (sender is TextBox textBox)
        {
            BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty)?.UpdateSource();
        }
        else if (sender is ComboBox comboBox)
        {
            BindingOperations.GetBindingExpression(comboBox, ComboBox.SelectedItemProperty)?.UpdateSource();
        }
    }

    private static int NormalizeSimulationHeight(int height)
    {
        int clamped = Math.Clamp(height, 72, 2160);
        int closest = LayerEditorOptions.SimulationHeightPresets[0];
        int bestDistance = Math.Abs(clamped - closest);
        for (int i = 1; i < LayerEditorOptions.SimulationHeightPresets.Count; i++)
        {
            int candidate = LayerEditorOptions.SimulationHeightPresets[i];
            int distance = Math.Abs(clamped - candidate);
            if (distance < bestDistance)
            {
                closest = candidate;
                bestDistance = distance;
            }
        }

        return closest;
    }

    private void SimulationLayerLifeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerBinningMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerInjectionNoise_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerLifeOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerHue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationLayerPixelSortGrid_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void AddSimulationReactiveMapping_Click(object sender, RoutedEventArgs e)
    {
        var layer = GetSelectedSimulationLayer();
        if (layer == null)
        {
            return;
        }

        var mapping = new LayerEditorSimulationReactiveMapping
        {
            Id = Guid.NewGuid(),
            Input = nameof(SimulationReactiveInput.Level),
            Output = nameof(SimulationReactiveOutput.Opacity),
            Amount = 1.0,
            ThresholdMin = 0.0,
            ThresholdMax = 1.0
        };
        mapping.PropertyChanged += ReactiveMapping_PropertyChanged;
        layer.ReactiveMappings.Add(mapping);
        layer.NotifyDetailsChanged();
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void RemoveSimulationReactiveMapping_Click(object sender, RoutedEventArgs e)
    {
        var layer = GetSelectedSimulationLayer();
        if (layer == null || sender is not FrameworkElement { DataContext: LayerEditorSimulationReactiveMapping mapping })
        {
            return;
        }

        mapping.PropertyChanged -= ReactiveMapping_PropertyChanged;
        layer.ReactiveMappings.Remove(mapping);
        if (layer.ReactiveMappings.Count == 0)
        {
            layer.AudioFrequencyHueShiftDegrees = 0;
        }
        layer.NotifyDetailsChanged();
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationReactiveMappingSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        GetSelectedSimulationLayer()?.NotifyDetailsChanged();
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationReactiveMappingAmount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GetSelectedSimulationLayer()?.NotifyDetailsChanged();
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void SimulationReactiveMappingThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GetSelectedSimulationLayer()?.NotifyDetailsChanged();
        if (ShouldApplyLive())
        {
            ApplySimulationLayerSettingsLive();
        }
    }

    private void ApplySimulationLayerSettingsLive(bool force = false)
    {
        if (_suppressSimulationLayerBindingApply && !force)
        {
            TraceSelection("ApplySimulationLayerSettingsLive suppressed during sim-layer selection bind.");
            return;
        }

        TraceSelection($"ApplySimulationLayerSettingsLive begin source={DescribeSource(_viewModel.SelectedSource)} layer={DescribeSimulationLayer(GetSelectedSimulationLayer())}");
        var simulationSource = GetSelectedSimulationSource();
        if (simulationSource == null)
        {
            return;
        }

        StripLegacyReactiveHueFields(simulationSource.SimulationLayers);
        _owner.UpdateSimulationGroupLayers(simulationSource.Id, simulationSource.SimulationLayers.ToList());
        TraceSelection($"ApplySimulationLayerSettingsLive end source={DescribeSource(_viewModel.SelectedSource)} layer={DescribeSimulationLayer(GetSelectedSimulationLayer())}");
    }

    private static void StripLegacyReactiveHueFields(IEnumerable<LayerEditorSimulationLayer> layers)
    {
        foreach (var layer in layers)
        {
            layer.AudioFrequencyHueShiftDegrees = 0;
            if (layer.Children.Count > 0)
            {
                StripLegacyReactiveHueFields(layer.Children);
            }
        }
    }

    private static ObservableCollection<LayerEditorSimulationLayer> GetSimulationParentCollection(
        LayerEditorSimulationLayer layer,
        ObservableCollection<LayerEditorSimulationLayer> rootLayers) =>
        layer.Parent?.Children ?? rootLayers;

    private (ObservableCollection<LayerEditorSimulationLayer> collection, int index, LayerEditorSimulationLayer? parent) ResolveSimulationInsertLocation(
        LayerEditorSimulationLayer? selected,
        ObservableCollection<LayerEditorSimulationLayer> rootLayers)
    {
        if (selected == null)
        {
            return (rootLayers, rootLayers.Count, null);
        }

        if (selected.IsGroup)
        {
            return (selected.Children, selected.Children.Count, selected);
        }

        var parentCollection = GetSimulationParentCollection(selected, rootLayers);
        int selectedIndex = parentCollection.IndexOf(selected);
        return (parentCollection, selectedIndex < 0 ? parentCollection.Count : selectedIndex + 1, selected.Parent);
    }

    private static int CountSimulationLeafLayers(LayerEditorSimulationLayer layer)
    {
        if (!layer.IsGroup)
        {
            return 1;
        }

        int count = 0;
        foreach (var child in layer.Children)
        {
            count += CountSimulationLeafLayers(child);
        }

        return count;
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
            if (source.Kind == LayerEditorSourceKind.Group)
            {
                _owner.UpdateGroupName(source.Id, source.DisplayName);
                return;
            }

            _owner.UpdateSourceDisplayName(source.Id, source.DisplayName);
        }
    }

    private void SimulationGroupEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (ShouldApplyLive())
        {
            var source = ResolveSourceContext(sender);
            if (source != null)
            {
                _owner.UpdateSourceEnabled(source.Id, source.Enabled);
            }
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
    private void AddRootSimGroup_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.SimGroup);

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

    private void AddChildSimGroup_Click(object sender, RoutedEventArgs e)
    {
        var parent = ResolveSelectedGroup(sender);
        if (parent != null)
        {
            AddSource(parent, LayerEditorSourceKind.SimGroup);
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

            case LayerEditorSourceKind.SimGroup:
                _owner.AddSimulationGroupFromEditor(parentId);
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

            case LayerEditorSourceKind.SimGroup:
            {
                return new LayerEditorSource
                {
                    Id = Guid.NewGuid(),
                    Kind = LayerEditorSourceKind.SimGroup,
                    DisplayName = "Sim Group",
                    Enabled = true,
                    BlendMode = "Additive",
                    FitMode = "Fill",
                    Opacity = 1.0,
                    KeyColorHex = "#000000",
                    KeyTolerance = 0.1
                };
            }

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

        TraceSelection($"SceneTree_SelectedItemChanged old={DescribeObject(e.OldValue)} new={DescribeObject(e.NewValue)}");
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

    private static LayerEditorSimulationLayer BuildDefaultSimulationLayer(string name, LayerEditorSimulationLayerType layerType)
    {
        return new LayerEditorSimulationLayer
        {
            Id = Guid.NewGuid(),
            LayerType = layerType,
            Name = name,
            Enabled = true,
            InputFunction = "Direct",
            BlendMode = layerType == LayerEditorSimulationLayerType.PixelSort ? "Normal" : "Additive",
            InjectionMode = "Threshold",
            LifeMode = "NaiveGrayscale",
            BinningMode = "Fill",
            InjectionNoise = 0.0,
            LifeOpacity = 1.0,
            RgbHueShiftDegrees = 0.0,
            RgbHueShiftSpeedDegreesPerSecond = 0.0,
            AudioFrequencyHueShiftDegrees = 0.0,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(),
            ThresholdMin = 0.35,
            ThresholdMax = 0.75,
            InvertThreshold = false,
            PixelSortCellWidth = 12,
            PixelSortCellHeight = 8
        };
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
