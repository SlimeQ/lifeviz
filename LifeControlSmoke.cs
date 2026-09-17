using System;
using System.Linq;

namespace lifeviz;

internal static class LifeControlSmoke
{
    public static void Run()
    {
        foreach(var mode in new[] { GameOfLifeEngine.LifeMode.NaiveGrayscale, GameOfLifeEngine.LifeMode.RgbChannels })
        foreach(int depth in new[] { 3,24,72,96 })
        {
            var cpu=new GameOfLifeEngine(); cpu.Configure(72,depth,16d/9); cpu.SetMode(mode);
            using var gpu=new GpuSimulationBackend(); gpu.Configure(72,depth,16d/9); gpu.SetMode(mode);
            var mask=new bool[cpu.Rows,cpu.Columns];
            // A stable block and an oscillator, including a border fixture.
            mask[20,20]=mask[20,21]=mask[21,20]=mask[21,21]=true;
            mask[40,40]=mask[40,41]=mask[40,42]=true;
            mask[0,0]=mask[0,1]=mask[1,0]=true;
            if(mode==GameOfLifeEngine.LifeMode.NaiveGrayscale) { cpu.InjectFrame(mask); gpu.InjectFrame(mask); }
            else { cpu.InjectRgbFrame(mask,mask,mask); gpu.InjectRgbFrame(mask,mask,mask); }
            var a=new byte[cpu.Rows*cpu.Columns*4]; var b=new byte[a.Length];
            for(int frame=0;frame<5;frame++)
            {
                foreach(var bin in Enum.GetValues<GameOfLifeEngine.BinningMode>())
                {
                    cpu.SetBinningMode(bin); gpu.SetBinningMode(bin);
                    cpu.FillColorBuffer(a); gpu.FillColorBuffer(b);
                    int delta=a.Select((v,i)=>Math.Abs(v-b[i])).Max();
                    if(delta>1) throw new InvalidOperationException($"Life CPU/GPU disagreement: {mode}, depth={depth}, {bin}, frame={frame}, maxDelta={delta}");
                }
                cpu.Step(); gpu.Step();
            }
            Logger.Info($"Life controls: {mode}, depth {depth}, Conway evolution and Fill/Binary CPU/GPU agreement passed.");
        }
    }
}
