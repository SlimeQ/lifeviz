using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    internal bool RunMappingOptionsSmoke()
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var expected = new Dictionary<LayerEditorSimulationLayerType, string[]>
        {
            [LayerEditorSimulationLayerType.ParticleErosion] = new[] { "ParticleEmission", "ParticleTurbulence" },
            [LayerEditorSimulationLayerType.RippleField] = new[] { "RippleImpulse", "RippleRefraction" },
            [LayerEditorSimulationLayerType.ChromaticMemory] = new[] { "ChromaticRed", "ChromaticGreen", "ChromaticBlue" },
            [LayerEditorSimulationLayerType.ContourCurrent] = new[] { "ContourFlow", "ContourThickness" },
            [LayerEditorSimulationLayerType.Life] = new[] { "InjectionNoise", "ThresholdMin", "ThresholdMax" },
            [LayerEditorSimulationLayerType.PixelSort] = new[] { "PixelSortCellWidth", "PixelSortCellHeight" },
            [LayerEditorSimulationLayerType.Datamosh] = new[] { "DatamoshFeedback", "DatamoshDisplacement" },
            [LayerEditorSimulationLayerType.FluidInk] = new[] { "FluidFlow" },
            [LayerEditorSimulationLayerType.TimeDisplacement] = new[] { "TimeSpread" },
            [LayerEditorSimulationLayerType.ReactionDiffusion] = new[] { "ReactionSeed" },
            [LayerEditorSimulationLayerType.FeedbackKaleidoscope] = new[] { "KaleidoscopeFeedback", "KaleidoscopeZoom", "KaleidoscopeRotation" }
        };
        string[] common = { "Opacity", "Framerate", "HueShift", "HueSpeed" };
        RefreshFromSources(); var source = EnsureSimulationSourceForSmoke()!; SetSelectedSource(source);
        SetLiveModeForSmoke(false); Show();
        foreach (var (type, specific) in expected)
        {
            AddSimulationLayer(type); var layer = GetSelectedSimulationLayer()!;
            layer.ReactiveMappings = new() { new() { Output = "HueShift", Amount = 42, ThresholdMin = 0.2, ThresholdMax = 0.8 } };
            var mapping = layer.ReactiveMappings[0];
            string[] wanted = common.Concat(specific).OrderBy(s => s).ToArray();
            Check(mapping.OutputOptions.Where(o => o.IsEnabled).Select(o => o.Value).OrderBy(s => s).SequenceEqual(wanted), $"Incorrect choices for {type}.");
            UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var combo = FieldSmokeVisuals(this).OfType<ComboBox>().Single(c => c.IsVisible && ReferenceEquals(c.DataContext, mapping) && c.GetBindingExpression(ComboBox.SelectedValueProperty)?.ParentBinding.Path.Path == "Output");
            combo.BringIntoView(); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Check((string?)combo.SelectedValue == "HueShift" && mapping.Amount == 42, "Binding refresh changed an existing mapping.");
            Check(combo.Items.Count == wanted.Length, "Dropdown did not filter its actual items.");
            combo.SetCurrentValue(ComboBox.SelectedValueProperty, specific[0]);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Check(mapping.Output == specific[0], "Filtered choice did not update the mapping.");
            Check(CloneSimulationLayer(layer).ReactiveMappings[0].OutputOptions.Count == wanted.Length, "Clone lost sim context.");
            if (type == LayerEditorSimulationLayerType.FluidInk)
            {
                mapping.Output = "DatamoshFeedback"; mapping.Amount = 0.63;
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(mapping.Output == "DatamoshFeedback" && mapping.Amount == 0.63 && (string?)combo.SelectedValue == "DatamoshFeedback", $"Old incompatible mapping changed: output={mapping.Output}, amount={mapping.Amount}, selected={combo.SelectedValue}.");
                Check(combo.Items.Cast<LayerEditorOption>().Single(o => o.Value == "DatamoshFeedback").IsEnabled == false, "Old incompatible mapping was offered as a new choice.");
                combo.SetCurrentValue(ComboBox.SelectedValueProperty, "FluidFlow");
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(mapping.Output == "FluidFlow" && combo.Items.Count == 5, "Replacing an incompatible mapping left a stale option.");
            }
            if (type is LayerEditorSimulationLayerType.FluidInk or LayerEditorSimulationLayerType.FeedbackKaleidoscope)
            {
                combo.IsDropDownOpen = true; UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                // Let the popup opening animation finish before capturing its contents.
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
                var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                var popup = (FrameworkElement)PresentationSource.FromVisual(item)!.RootVisual;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth), (int)Math.Ceiling(popup.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(popup); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, $"smoke-{type}-mapping-options.png"))) png.Save(file);
                combo.IsDropDownOpen = false;
            }
        }
        var compatibility = new LayerEditorSimulationLayer { Id = Guid.NewGuid(), LayerType = LayerEditorSimulationLayerType.FluidInk,
            ReactiveMappings = new() { new() { Output = "DatamoshFeedback", Amount = 0.73 } } };
        compatibility.LayerType = LayerEditorSimulationLayerType.Datamosh;
        Check(compatibility.ReactiveMappings[0].OutputOptions.All(o => o.IsEnabled), "Changing sim type did not refresh supported status.");
        compatibility.LayerType = LayerEditorSimulationLayerType.FluidInk;
        source.SimulationLayers.Add(compatibility);
        var project = LayerConfigFile.FromEditorSources(_viewModel.Sources, Array.Empty<LayerEditorSimulationLayer>(), _owner.GetProjectSettingsForEditor());
        var loaded = LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources();
        var old = EnumerateSources(loaded).SelectMany(s => s.SimulationLayers).Single(s => s.Id == compatibility.Id).ReactiveMappings[0];
        Check(old.Output == "DatamoshFeedback" && old.Amount == 0.73 && old.OutputOptions.Any(o => o.Value == old.Output && !o.IsEnabled), "Project load discarded an incompatible mapping.");
        Logger.Info("Reactive output filter smoke passed: all eleven types, real dropdowns, selection changes, collection replacement, clones and incompatible saved mappings.");
        return true;
    }
}
