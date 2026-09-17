using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal bool RunAudioResponseEditorSmoke()
    {
        static void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
        RefreshFromSources(); var source=EnsureSimulationSourceForSmoke()!; SetSelectedSource(source);
        SetLiveModeForSmoke(true); AddSimulationLayer(LayerEditorSimulationLayerType.ChromaticMemory);
        Show(); UpdateLayout(); Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        var layer=GetSelectedSimulationLayer()!; var id=layer.Id; var mapping=layer.ReactiveMappings[0];
        LayerEditorSimulationReactiveMapping Runtime()
        {
            _owner.GetSimulationLayerSettingsForEditor(out var layers);
            return EnumerateSimulationLayers(layers).Single(l=>l.Id==id).ReactiveMappings.Single(m=>m.Id==mapping.Id);
        }
        Slider SliderFor(string name) => FieldSmokeVisuals(this).OfType<Slider>().Single(s=>s.IsVisible && ReferenceEquals(s.DataContext,mapping) && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path==name);
        Check(SliderFor("Amount").Minimum<0,"Strength slider cannot pull down a control.");
        SliderFor("Amount").SetCurrentValue(Slider.ValueProperty,-0.4);
        SliderFor("AttackMs").SetCurrentValue(Slider.ValueProperty,12d);
        SliderFor("ReleaseMs").SetCurrentValue(Slider.ValueProperty,140d);
        Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        Check(Runtime().Amount==-0.4 && Runtime().AttackMs==12 && Runtime().ReleaseMs==140,"Latest live response values were not applied.");
        SetLiveModeForSmoke(false);
        SliderFor("ReleaseMs").SetCurrentValue(Slider.ValueProperty,230d);
        Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        Check(Runtime().ReleaseMs==140,"Draft response leaked to runtime.");
        ApplyButton_Click(ApplyButton,new RoutedEventArgs(Button.ClickEvent,ApplyButton));
        Check(Runtime().ReleaseMs==230,"Draft response was not applied.");
        layer=GetSelectedSimulationLayer()!; mapping=layer.ReactiveMappings[0];
        var project=LayerConfigFile.FromEditorSources(_viewModel.Sources,Array.Empty<LayerEditorSimulationLayer>(),_owner.GetProjectSettingsForEditor());
        var saved=EnumerateSources(LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources()).SelectMany(s=>EnumerateSimulationLayers(s.SimulationLayers)).Single(l=>l.Id==id).ReactiveMappings[0];
        Check(saved.Amount==-0.4 && saved.AttackMs==12 && saved.ReleaseMs==230,"Scene lost response settings.");
        Check(CloneSimulationLayer(layer).ReactiveMappings[0].ReleaseMs==230,"Draft clone lost response settings.");
        string before=JsonSerializer.Serialize(layer.Effects);
        var originalMappingId = mapping.Id;
        PunchySimulationMappings_Click(this,new RoutedEventArgs());
        Check(layer.ReactiveMappings.Count==3 && layer.ReactiveMappings.All(m=>m.Amount<0) && JsonSerializer.Serialize(layer.Effects)==before,"Preset changed base controls or failed to replace mappings.");
        Check(Runtime().Id == originalMappingId && Runtime().ReleaseMs == 230 && Runtime().Amount == -0.4,
            "Draft preset altered live mappings.");
        UpdateLayout(); Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        var responseSlider=FieldSmokeVisuals(this).OfType<Slider>().First(s=>s.IsVisible && s.GetBindingExpression(Slider.ValueProperty)?.ParentBinding.Path.Path=="ReleaseMs");
        responseSlider.BringIntoView(); UpdateLayout(); Dispatcher.Invoke(()=>{},DispatcherPriority.Background);
        var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(this);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"smoke-audio-response-editor.png"))) png.Save(file);
        Logger.Info("Audio response editor signed strength, attack/release, live/draft behavior, presets and roundtrip passed.");
        return true;
    }
}
