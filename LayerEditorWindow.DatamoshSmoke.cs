using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal bool RunDatamoshEditorSmoke()
    {
        RefreshFromSources();
        var source = EnsureSimulationSourceForSmoke();
        if (source == null) return false;
        SetSelectedSource(source);
        AddSimulationLayer(LayerEditorSimulationLayerType.Datamosh);
        var layer = GetSelectedSimulationLayer();
        if (layer == null) return false;
        Guid id = layer.Id;
        bool defaults = layer.IsDatamoshLayer && layer.BlendMode == "Normal" && layer.ReactiveMappings.Count == 2;
        layer.DatamoshFeedback = 0.62;
        layer.DatamoshDisplacement = 0.37;
        layer.DatamoshBlockSize = 23;
        ApplySimulationLayerSettingsLive(force: true);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        RefreshFromSources(source.Id);
        var refreshedSource = EnumerateSources(_viewModel.Sources).Single(s => s.Id == source.Id);
        var refreshed = FindSimulationLayerById(refreshedSource.SimulationLayers, id);
        bool Matches(LayerEditorSimulationLayer? candidate) => candidate?.IsDatamoshLayer == true &&
            candidate.DatamoshFeedback == 0.62 && candidate.DatamoshDisplacement == 0.37 &&
            candidate.DatamoshBlockSize == 23 && candidate.ReactiveMappings.Count == 2;
        bool runtime = _owner.TryGetSimulationLayerRuntimeInfoForSmoke(id, out string type, out _, out _) && type == "Datamosh";
        var project = LayerConfigFile.FromEditorSources(_viewModel.Sources, Array.Empty<LayerEditorSimulationLayer>(), _owner.GetProjectSettingsForEditor());
        var savedSources = LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources();
        // Project loads intentionally assign fresh source IDs; simulation IDs survive.
        var savedSource = EnumerateSources(savedSources).Single(s => s.IsSimulationGroup &&
            FindSimulationLayerById(s.SimulationLayers, id) != null);
        bool saved = Matches(FindSimulationLayerById(savedSource.SimulationLayers, id));
        bool draftClone = Matches(CloneSimulationLayer(layer));
        bool refreshedOk = Matches(refreshed);
        Logger.Info($"Datamosh editor smoke: defaults={defaults}, runtime={runtime}, refresh={refreshedOk}, project={saved}, draftClone={draftClone}.");
        return defaults && runtime && refreshedOk && saved && draftClone;
    }
}
