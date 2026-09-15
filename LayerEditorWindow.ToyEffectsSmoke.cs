using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal bool RunToyEffectsEditorSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        RefreshFromSources(); var source = EnsureSimulationSourceForSmoke()!; SetSelectedSource(source);
        foreach (var (kind, prefix, count) in new[]
        {
            (LayerEditorSimulationLayerType.ParticleErosion, "Particle", 2),
            (LayerEditorSimulationLayerType.RippleField, "Ripple", 2),
            (LayerEditorSimulationLayerType.ChromaticMemory, "Chromatic", 3),
            (LayerEditorSimulationLayerType.ContourCurrent, "Contour", 2)
        })
        {
            SetLiveModeForSmoke(true); AddSimulationLayer(kind); var layer = GetSelectedSimulationLayer()!; var id = layer.Id;
            Check(layer.LayerType == kind && layer.BlendMode == "Normal" && layer.ReactiveMappings.Count == count, "Incorrect new layer defaults.");
            Check(layer.ReactiveMappings.All(m => m.OutputOptions.Single(o => o.Value == m.Output).IsEnabled), "Default mapping is not supported.");
            Show(); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var sliders = FieldSmokeVisuals(this).OfType<Slider>().Where(s => s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path.StartsWith("Effects." + prefix) == true).ToArray();
            Check(sliders.Length == (kind == LayerEditorSimulationLayerType.ContourCurrent ? 3 : 4), "Missing controls.");
            foreach (string name in sliders.Select(s => s.GetBindingExpression(Slider.ValueProperty)!.ParentBinding.Path.Path[8..]).ToArray())
            {
                // Apply rebuilds the editor tree; reacquire the visible model/control each time.
                SetLiveModeForSmoke(true);
                layer = GetSelectedSimulationLayer()!;
                UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                var slider = FieldSmokeVisuals(this).OfType<Slider>().Single(s => s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path == "Effects." + name);
                var property = typeof(SimulationEffectSettings).GetProperty(name)!;
                double Runtime()
                {
                    _owner.GetSimulationLayerSettingsForEditor(out var layers);
                    return (double)property.GetValue(EnumerateSimulationLayers(layers).Single(s => s.Id == id).Effects)!;
                }
                slider.SetCurrentValue(Slider.ValueProperty, slider.Minimum + (slider.Maximum-slider.Minimum)*0.3);
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                double live = (double)property.GetValue(layer.Effects)!;
                Check(Math.Abs(Runtime()-live) < 1e-6, $"{name} did not apply live: editor={live}, runtime={Runtime()}.");
                SetLiveModeForSmoke(false);
                slider.SetCurrentValue(Slider.ValueProperty, slider.Minimum + (slider.Maximum-slider.Minimum)*0.7);
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(Math.Abs(Runtime()-live) < 1e-6, name + " escaped draft isolation.");
                ApplyButton_Click(ApplyButton, new RoutedEventArgs(Button.ClickEvent, ApplyButton));
                Check(Math.Abs(Runtime()-(double)property.GetValue(layer.Effects)!) < 1e-6, name + " did not apply draft.");
            }
            layer = GetSelectedSimulationLayer()!;
            var clone = CloneSimulationLayer(layer);
            string expected = JsonSerializer.Serialize(layer.Effects);
            Check(JsonSerializer.Serialize(clone.Effects) == expected, "Clone lost controls.");
            clone.Effects.ParticleGravity = -1; Check(layer.Effects.ParticleGravity != -1, "Draft shares settings.");
            var project = LayerConfigFile.FromEditorSources(_viewModel.Sources, Array.Empty<LayerEditorSimulationLayer>(), _owner.GetProjectSettingsForEditor());
            Check(project.Version == 15, "Scene version was not advanced.");
            var loaded = LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources();
            var saved = EnumerateSources(loaded).Where(s => s.IsSimulationGroup).SelectMany(s => EnumerateSimulationLayers(s.SimulationLayers)).Single(s => s.Id == id);
            Check(saved.LayerType == kind && JsonSerializer.Serialize(saved.Effects) == expected && saved.ReactiveMappings.Count == count, "Scene roundtrip lost settings or mappings.");
            FieldSmokeVisuals(this).OfType<Slider>().Last(s => s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path.StartsWith("Effects." + prefix) == true).BringIntoView(); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, $"smoke-{kind}-editor.png"))) png.Save(file);
            Logger.Info(kind + " editor controls, live/draft isolation, defaults and persistence passed.");
        }
        return true;
    }
}
