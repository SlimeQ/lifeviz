using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal bool RunFieldEffectsEditorSmoke(bool kaleidoscope = false)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        RefreshFromSources();
        var source = EnsureSimulationSourceForSmoke()!;
        SetSelectedSource(source);
        foreach (var kind in kaleidoscope ? new[] { LayerEditorSimulationLayerType.FeedbackKaleidoscope } : new[] { LayerEditorSimulationLayerType.FluidInk, LayerEditorSimulationLayerType.TimeDisplacement, LayerEditorSimulationLayerType.ReactionDiffusion })
        {
            AddSimulationLayer(kind);
            var layer = GetSelectedSimulationLayer()!;
            Guid id = layer.Id;
            Check(layer.LayerType == kind && layer.BlendMode == "Normal" && layer.ReactiveMappings.Count == (kaleidoscope ? 2 : 1), "Incorrect defaults.");
            layer.Effects.KaleidoscopeFeedback = 0.77; layer.Effects.KaleidoscopeZoom = 0.985; layer.Effects.KaleidoscopeRotation = -2.3; layer.Effects.KaleidoscopeFolds = 9; layer.Effects.KaleidoscopeCenterX = 0.4; layer.Effects.KaleidoscopeCenterY = 0.6;
            layer.Effects.FluidFlow = 0.71; layer.Effects.TimeSpread = 0.37; layer.Effects.ReactionFeed = 0.041;
            ApplySimulationLayerSettingsLive(force: true);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            RefreshFromSources(source.Id);
            source = EnumerateSources(_viewModel.Sources).Single(s => s.Id == source.Id);
            SetSelectedSource(source);
            var refreshed = FindSimulationLayerById(source.SimulationLayers, id)!;
            Check(_owner.TryGetSimulationLayerRuntimeInfoForSmoke(id, out string type, out _, out _) && type == kind.ToString(), "Runtime lost effect type.");
            bool Matches(LayerEditorSimulationLayer candidate) => candidate.LayerType == kind && candidate.Effects.FluidFlow == 0.71 && candidate.Effects.TimeSpread == 0.37 && candidate.Effects.ReactionFeed == 0.041 && candidate.Effects.KaleidoscopeFeedback == 0.77 && candidate.Effects.KaleidoscopeZoom == 0.985 && candidate.Effects.KaleidoscopeRotation == -2.3 && candidate.Effects.KaleidoscopeFolds == 9 && candidate.Effects.KaleidoscopeCenterX == 0.4 && candidate.Effects.KaleidoscopeCenterY == 0.6;
            Check(Matches(refreshed), "Runtime refresh lost controls.");
            var project = LayerConfigFile.FromEditorSources(_viewModel.Sources, Array.Empty<LayerEditorSimulationLayer>(), _owner.GetProjectSettingsForEditor());
            var roundtrip = LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources();
            var saved = EnumerateSources(roundtrip).Where(s => s.IsSimulationGroup).Select(s => FindSimulationLayerById(s.SimulationLayers, id)).First(s => s != null)!;
            Check(Matches(saved) && Matches(CloneSimulationLayer(refreshed)), "Project/draft roundtrip lost controls.");
            var clone = CloneSimulationLayer(refreshed); clone.Effects.TimeSpread = 0;
            Check(refreshed.Effects.TimeSpread == 0.37, "Draft modifies live settings.");
            SetSelectedSimulationLayer(refreshed);
            Show(); UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            string property = kind switch { LayerEditorSimulationLayerType.FeedbackKaleidoscope => "KaleidoscopeFeedback", LayerEditorSimulationLayerType.FluidInk => "FluidFlow", LayerEditorSimulationLayerType.TimeDisplacement => "TimeSpread", _ => "ReactionSeed" };
            var slider = FieldSmokeVisuals(this).OfType<Slider>().First(s => s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path == "Effects." + property);
            double RuntimeValue()
            {
                _owner.GetSimulationLayerSettingsForEditor(out var layers);
                var runtime = EnumerateSimulationLayers(layers).Single(s => s.Id == id).Effects;
                return kind switch { LayerEditorSimulationLayerType.FeedbackKaleidoscope => runtime.KaleidoscopeFeedback, LayerEditorSimulationLayerType.FluidInk => runtime.FluidFlow, LayerEditorSimulationLayerType.TimeDisplacement => runtime.TimeSpread, _ => runtime.ReactionSeed };
            }
            slider.SetCurrentValue(Slider.ValueProperty, 0.25);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Check(Math.Abs(RuntimeValue() - 0.25) < 1e-6, "Slider did not apply its latest value in Live Mode.");
            SetLiveModeForSmoke(false);
            slider.SetCurrentValue(Slider.ValueProperty, 0.6);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Check(Math.Abs(RuntimeValue() - 0.25) < 1e-6, "Draft slider changed the live layer before Apply.");
            ApplyButton_Click(ApplyButton, new RoutedEventArgs(Button.ClickEvent, ApplyButton));
            Check(Math.Abs(RuntimeValue() - 0.6) < 1e-6, "Apply did not commit draft controls.");
            SetLiveModeForSmoke(true);
            slider.BringIntoView();
            if (kaleidoscope)
                FieldSmokeVisuals(this).OfType<Slider>().First(s => s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path == "Effects.KaleidoscopeCenterY").BringIntoView();
            UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, $"smoke-{kind}-editor.png")); png.Save(file);
        }
        Logger.Info("Field effects editor defaults, live updates, isolated drafts and scene roundtrips passed.");
        return true;
    }

    private static IEnumerable<DependencyObject> FieldSmokeVisuals(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in FieldSmokeVisuals(child)) yield return descendant;
        }
    }
}
