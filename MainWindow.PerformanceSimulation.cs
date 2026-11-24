using System;

namespace lifeviz;

public partial class MainWindow
{
    private void UpdatePerformanceSimulation(SimulationLayerState layer, GpuPixelSortBackend backend,
        double dt, bool audio, long beat, ReadOnlySpan<double> inputs)
    {
        if (!layer.Enabled)
        {
            layer.Performance = null;
            backend.SetPerformanceFrame(default);
            return;
        }

        // Offline replay may use captured audio, but never an operator's live gamepad.
        LiveVjFrame vj = _isOfflineRendering ? default : GetLiveVjFrame(layer.Id);
        if (layer.Performance?.Effect != backend.Effect)
            layer.Performance = new PerformanceSimulationState(backend.Effect, vj.ReleaseSerial, vj.SlamSerial);

        backend.SetPerformanceFrame(layer.Performance.Update(dt, audio, beat,
            inputs[(int)SimulationReactiveInput.Bass], inputs[(int)SimulationReactiveInput.Mid],
            inputs[(int)SimulationReactiveInput.High], vj.Held, vj.ReleaseSerial, vj.SlamSerial));
    }
}
