using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace lifeviz;

public partial class MainWindow
{
    internal void SetReactiveMeterForSmoke()
    {
        _selectedAudioDeviceId="smoke"; _fastAudioLevel=0.7;
        _audioBeatDetector.SetSmokeReactiveState(0.7,0.7,0.55,0.35,0,0,0,0);
        ApplySimulationLayerReactiveState(1d/60);
    }
    internal bool RunSimulationControlSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        if (!_renderLoopAttached) InitializeVisualizer();
        _sources.Clear(); ClearSimulationLayers();
        _effectiveLifeOpacity = _lifeOpacity = 1; _isPaused = false;
        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns, height = GetReferenceSimulationEngine().Rows;
        var pixels = new byte[width * height * 4];
        for (int y=0;y<height;y++) for(int x=0;x<width;x++)
        {
            int i=(y*width+x)*4;
            pixels[i]=(byte)(30+200*(0.5+0.5*Math.Sin(x*0.14+y*0.07)));
            pixels[i+1]=(byte)(20+210*(0.5+0.5*Math.Cos(y*0.17)));
            pixels[i+2]=(byte)(20+210*(0.5+0.5*Math.Sin(x*0.09-y*0.11)));
            pixels[i+3]=255;
        }
        var source=CaptureSource.CreateFile("control-fixture","Control fixture",width,height);
        source.LastFrame=new SourceFrame(pixels,width,height,null,width,height); source.BlendMode=BlendMode.Normal;
        var group=CaptureSource.CreateSimulationGroup("Fluid control audit"); group.BlendMode=BlendMode.Normal;
        var spec=new SimulationLayerSpec { Id=Guid.NewGuid(),LayerType=SimulationLayerType.FluidInk,Name="Fluid",BlendMode=BlendMode.Normal,LifeOpacity=1 };
        group.SimulationLayers.Add(spec); _sources.Add(source); _sources.Add(group);
        ApplySimulationLayersFromSourceStack(false);
        var layer=FindSimulationNode(spec.Id)!;
        var backend=(GpuPixelSortBackend)layer.Engine!;
        var folder=Path.Combine(AppContext.BaseDirectory,"control-audit"); Directory.CreateDirectory(folder);
        var onsetFrames=new Dictionary<string,byte[]>();
        byte[] Frame(int frame)
        {
            layer.TimeSinceLastStep=1d/60; layer.EffectiveSimulationTargetFps=60;
            byte[]? buffer=null; bool injected=false; int stepped=0;
            return BuildInlineCompositeFrameGpu(_sources,ref buffer,true,frame/60d,1,ref injected,ref stepped,true)!.Downscaled.ToArray();
        }
        byte[] Run(string label, double flow,double persistence,double swirl, bool audio=false)
        {
            backend.Randomize(); layer.ReactiveEnvelopes.Clear();
            layer.Effects.FluidFlow=flow; layer.Effects.FluidPersistence=persistence; layer.Effects.FluidSwirl=swirl;
            layer.ReactiveMappings=NormalizeReactiveMappings(SimulationReactivePresets.Create(new LayerEditorSimulationLayer
                { LayerType=LayerEditorSimulationLayerType.FluidInk, Effects=layer.Effects.Clone() }),0);
            _selectedAudioDeviceId="smoke";
            _audioBeatDetector.BeginOfflineInput(); _audioBeatDetector.SetAnalysisRequirements(true,false);
            byte[] result=Array.Empty<byte>();
            for(int f=0;f<100;f++)
            {
                var pcm=new float[800];
                if(audio && f>=60 && f%30<8)
                    for(int j=0;j<pcm.Length;j++) pcm[j]=(float)(0.22*(Math.Sin(2*Math.PI*110*(f*800+j)/48000d)+0.5*Math.Sin(2*Math.PI*880*(f*800+j)/48000d)));
                _audioBeatDetector.ProcessOfflineSamples(pcm,f/60d); _fastAudioLevel=_audioBeatDetector.EnvelopeEnergy;
                ApplySimulationLayerReactiveState(1d/60);
                result=Frame(f);
                if(f==61) onsetFrames[label]=result;
            }
            SaveFieldSmokeImage(result,width,height,Path.Combine(folder,label+".png")); return result;
        }
        double Difference(byte[] a,byte[] b) => a.Select((v,i)=>i%4==3 ? 0 : Math.Abs(v-b[i])).Sum()/(width*height*3d);
        var baseline=Run("baseline",0.45,0.94,0.45);
        foreach(var test in new[] { ("flow-zero",0d,0.94,0.45),("flow-full",1d,0.94,0.45),("persistence-zero",0.45,0d,0.45),("swirl-zero",0.45,0.94,0d),("swirl-full",0.45,0.94,1d) })
        {
            var output=Run(test.Item1,test.Item2,test.Item3,test.Item4);
            double delta=Difference(baseline,output);
            Check(delta>8,$"Fluid {test.Item1} has insufficient visible range: {delta:F3}");
            if(test.Item1 is "flow-zero" or "persistence-zero") Check(Difference(pixels,output)<0.01,"Fluid zero endpoint failed.");
            Logger.Info($"Control audit {test.Item1}: mean RGB delta={delta:F3}");
        }
        var driven=Run("audio",0.45,0.94,0.45,true);
        double audioDelta=Difference(baseline,driven);
        double onsetDelta=Difference(onsetFrames["baseline"],onsetFrames["audio"]);
        Check(audioDelta>10,"PCM/preset/inline GPU path has insufficient visible response.");
        Check(onsetDelta>1,"Fluid failed to respond visibly within two rendered frames of PCM onset.");
        Check(driven.SequenceEqual(Run("audio-repeat",0.45,0.94,0.45,true)),"Audio replay is not deterministic.");
        Logger.Info($"Control audit real PCM through preset to inline GPU: mean RGB delta={audioDelta:F3}, two-frame onset delta={onsetDelta:F3}");
        layer.ReactiveMappings=new() { new() { Input=SimulationReactiveInput.Level,Output=SimulationReactiveOutput.Opacity,Amount=1,AttackMs=0,ReleaseMs=0 } };
        double previous=-1;
        foreach(double level in new[] { 0d,0.001,0.25,0.5,1d })
        {
            _fastAudioLevel=level; _audioBeatDetector.SetSmokeReactiveState(level,0,0,0,0,0,0,0); ApplySimulationLayerReactiveState(1d/60);
            _isPaused=true; byte[] rendered=Frame(100);
            double difference=Difference(rendered,pixels);
            Check(difference>=previous,"Positive Level → Opacity inverted the effect."); previous=difference;
            if(level==0) Check(difference==0,"Zero opacity must reveal the source underneath.");
            Check(GetSimulationMappingStatus(layer.Id,layer.ReactiveMappings[0].Id).Contains("Live output"),"Mapping readout is missing.");
            Logger.Info($"Control audit Level={level:F3}, opacity={layer.EffectiveLifeOpacity:F3}, difference from unprocessed scene={Difference(rendered,pixels):F3}");
        }
        layer.ReactiveMappings[0].Amount=-1;
        previous=double.MaxValue;
        foreach(double level in new[] {0d,0.25,0.5,1d})
        {
            _fastAudioLevel=level; _audioBeatDetector.SetSmokeReactiveState(level,0,0,0,0,0,0,0); ApplySimulationLayerReactiveState(1d/60);
            double difference=Difference(Frame(100),pixels);
            Check(difference<=previous,"Negative opacity mapping did not fade out on loud audio."); previous=difference;
        }
        _isPaused=false; layer.AccumulatedHueDegrees=0; layer.LifeMode=GameOfLifeEngine.LifeMode.RgbChannels;
        layer.EffectiveRgbHueShiftSpeedDegreesPerSecond=90; AdvanceSimulationLayerHue(0.5);
        Check(Math.Abs(CurrentRgbHueShiftDegrees(layer)-45)<0.001,"Hue speed did not integrate elapsed time.");
        layer.EffectiveRgbHueShiftSpeedDegreesPerSecond=-90; AdvanceSimulationLayerHue(0.5);
        Check(Math.Abs(CurrentRgbHueShiftDegrees(layer))<0.001,"Hue speed changed phase rather than direction.");
        layer.ReactiveMappings=new() {new() { Input=SimulationReactiveInput.Level,Output=SimulationReactiveOutput.ThresholdMin,Amount=1,AttackMs=0 }};
        _fastAudioLevel=1; ApplySimulationLayerReactiveState(1d/60);
        Check(layer.EffectiveThresholdMin==layer.EffectiveThresholdMax && layer.EffectiveThresholdMax==layer.ThresholdMax,"Lower threshold crossed and reopened the window.");
        layer.ReactiveMappings[0].Output=SimulationReactiveOutput.ThresholdMax; ApplySimulationLayerReactiveState(1d/60);
        Check(layer.EffectiveThresholdMin==layer.EffectiveThresholdMax && layer.EffectiveThresholdMin==layer.ThresholdMin,"Upper threshold crossed and reopened the window.");
        LifeControlSmoke.Run();
        RunLifeInjectionControlSmoke();
        return true;
    }

    private void RunLifeInjectionControlSmoke()
    {
        using var gpu=new GpuSimulationBackend(); gpu.Configure(72,24,16d/9);
        int w=gpu.Columns,h=gpu.Rows; var pixels=new byte[w*h*4];
        for(int y=0;y<h;y++) for(int x=0;x<w;x++)
        {
            int i=(y*w+x)*4; pixels[i]=pixels[i+1]=pixels[i+2]=(byte)(x*255/(w-1)); pixels[i+3]=255;
        }
        var surface=_gpuSimulationGroupCompositor.UploadInputSurface(pixels,w,h)!;
        double Density(GameOfLifeEngine.LifeMode mode,double min,double max,double dropout=0,bool invert=false)
        {
            gpu.Configure(72,24,16d/9); gpu.SetMode(mode);
            if(!gpu.TryInjectCompositeSurface(surface,min,max,invert,GameOfLifeEngine.InjectionMode.Threshold,dropout,24,0,false)) throw new InvalidOperationException("Life injection failed.");
            var output=new byte[pixels.Length]; gpu.FillColorBuffer(output);
            return Enumerable.Range(0,w*h).Count(i=>output[i*4]+output[i*4+1]+output[i*4+2]>0)/(double)(w*h);
        }
        foreach(var mode in Enum.GetValues<GameOfLifeEngine.LifeMode>())
        {
            double normal=Density(mode,0.25,0.75), narrow=Density(mode,0.4,0.6), dropped=Density(mode,0.25,0.75,1), inverted=Density(mode,0.25,0.75,0,true);
            if(dropped!=0) throw new InvalidOperationException("100% dropout did not block injection.");
            if(mode!=GameOfLifeEngine.LifeMode.Bitwise && !(normal>0.45 && normal<0.55 && narrow<normal*0.5 && Math.Abs(normal+inverted-1)<0.01))
                throw new InvalidOperationException($"Life threshold/invert controls failed: {mode}, {normal}, {narrow}, {inverted}");
            if(mode==GameOfLifeEngine.LifeMode.Bitwise && normal!=narrow) throw new InvalidOperationException("Bitwise unexpectedly used thresholds.");
            Logger.Info($"Life injection {mode}: window density {normal:F3}, narrower {narrow:F3}, inverted {inverted:F3}, full dropout {dropped:F3}.");
        }
    }
}
