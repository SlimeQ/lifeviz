using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace lifeviz;

// Full-color effects share the established input, alpha, publication and readback path.
internal sealed partial class GpuPixelSortBackend
{
    public ImageSimulationEffect Effect { get; }
    public bool IsFieldEffect => Effect is ImageSimulationEffect.FluidInk or ImageSimulationEffect.TimeDisplacement or ImageSimulationEffect.ReactionDiffusion;
    private readonly SimulationEffectSettings _effects = new();
    private ID3D11ComputeShader? _advanceFieldShader, _captureHistoryShader;
    private ID3D11Buffer? _effectBuffer;
    private ID3D11Texture2D? _fieldA, _fieldB, _timeHistory;
    private ID3D11ShaderResourceView? _fieldSrvA, _fieldSrvB, _timeHistorySrv;
    private ID3D11UnorderedAccessView? _fieldUavA, _fieldUavB, _timeHistoryUav;
    private bool _fieldAIsSource = true, _fieldReady;
    private int _fieldWidth, _fieldHeight, _historyHead, _historyCount;
    internal const int HistoryCapacity = 64;
    internal long HistoryBytes => Effect == ImageSimulationEffect.TimeDisplacement ? (long)_fieldWidth * _fieldHeight * 4 * HistoryCapacity : 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct EffectParameters
    {
        public uint FieldWidth, FieldHeight, HistoryHead, HistoryCount;
        public float Flow, Persistence, Swirl, Spread;
        public float Scale, Motion, Feed, Kill;
        public float Seed;
        public uint Effect, Pass, FieldReady;
    }

    public void SetEffectSettings(SimulationEffectSettings settings)
    {
        lock (_sync) _effects.CopyFrom(settings);
    }

    private void InitializeFieldShaders()
    {
        if (!IsFieldEffect) return;
        _advanceFieldShader = _device!.CreateComputeShader(LoadShaderBytecode("Assets/GpuAdvanceFieldCS.cso"));
        _captureHistoryShader = _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuCaptureHistoryCS.cso"));
        _effectBuffer = _device.CreateBuffer((uint)Marshal.SizeOf<EffectParameters>(), BindFlags.ConstantBuffer,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private void EnsureFieldTextures()
    {
        if (!IsFieldEffect || _fieldA != null || _timeHistory != null) return;
        // Bound both solver work and history memory, including very wide scenes.
        double scale = Math.Min(1, Math.Min(360d / _rows, Math.Sqrt(262144d / ((double)_columns * _rows))));
        _fieldWidth = Math.Max(1, (int)(_columns * scale));
        _fieldHeight = Math.Max(1, (int)(_rows * scale));
        if (Effect == ImageSimulationEffect.TimeDisplacement)
        {
            _timeHistory = _device!.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UInt,
                (uint)_fieldWidth, (uint)_fieldHeight, (uint)HistoryCapacity, 1,
                BindFlags.ShaderResource | BindFlags.UnorderedAccess));
            _timeHistorySrv = _device.CreateShaderResourceView(_timeHistory);
            _timeHistoryUav = _device.CreateUnorderedAccessView(_timeHistory);
        }
        else
        {
            var desc = new Texture2DDescription(Format.R32G32B32A32_Float, (uint)_fieldWidth, (uint)_fieldHeight,
                1, 1, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
            _fieldA = _device!.CreateTexture2D(desc); _fieldB = _device.CreateTexture2D(desc);
            _fieldSrvA = _device.CreateShaderResourceView(_fieldA); _fieldSrvB = _device.CreateShaderResourceView(_fieldB);
            _fieldUavA = _device.CreateUnorderedAccessView(_fieldA); _fieldUavB = _device.CreateUnorderedAccessView(_fieldB);
        }
        ResetFields();
    }

    private void ResetFields()
    {
        _fieldReady = false; _fieldAIsSource = true; _historyHead = 0; _historyCount = 0;
        // History is never sampled outside the initialized count. No 64-slice clear required.
        if (_fieldUavA != null) _context!.ClearUnorderedAccessView(_fieldUavA, System.Numerics.Vector4.Zero);
        if (_fieldUavB != null) _context!.ClearUnorderedAccessView(_fieldUavB, System.Numerics.Vector4.Zero);
    }

    private void UploadEffectParameters(uint pass = 0)
    {
        var p = new EffectParameters
        {
            FieldWidth = (uint)_fieldWidth, FieldHeight = (uint)_fieldHeight,
            HistoryHead = (uint)_historyHead, HistoryCount = (uint)_historyCount,
            Flow = (float)_effects.FluidFlow, Persistence = (float)_effects.FluidPersistence, Swirl = (float)_effects.FluidSwirl,
            Spread = (float)_effects.TimeSpread, Scale = (float)_effects.TimeScale, Motion = (float)_effects.TimeMotion,
            Feed = (float)_effects.ReactionFeed, Kill = (float)_effects.ReactionKill, Seed = (float)_effects.ReactionSeed,
            Effect = (uint)Effect, Pass = pass, FieldReady = _fieldReady ? 1u : 0u
        };
        _context!.UpdateSubresource(in p, _effectBuffer!);
        _context.CSSetConstantBuffers(1, new[] { _effectBuffer! });
    }

    private ID3D11ShaderResourceView? CurrentField => _fieldAIsSource ? _fieldSrvA : _fieldSrvB;

    private void StepFields()
    {
        UploadParameters(0, 0);
        _context!.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
        if (Effect == ImageSimulationEffect.TimeDisplacement)
        {
            UploadEffectParameters();
            _context.CSSetShader(_captureHistoryShader);
            _context.CSSetShaderResources(1, new[] { _snapshotSrv! });
            _context.CSSetUnorderedAccessViews(2, new[] { _timeHistoryUav! });
            DispatchGrid(_context, _fieldWidth, _fieldHeight);
            _context.CSSetUnorderedAccessViews(2, new ID3D11UnorderedAccessView[] { null! });
            _context.CSSetShaderResources(1, new ID3D11ShaderResourceView[] { null! });
            _historyCount = Math.Min(HistoryCapacity, _historyCount + 1);
        }
        else if (Effect == ImageSimulationEffect.FluidInk)
        {
            AdvanceField(0); // advect velocity and add source-driven forces
            AdvanceField(1); // divergence
            for (int i = 0; i < 12; i++) AdvanceField(2); // Jacobi pressure solve
            AdvanceField(3); // project velocity
        }
        else
        {
            // Fixed, stable Euler substeps. Chemical state is float, never color-quantized.
            for (int i = 0; i < 8; i++) AdvanceField(4);
        }
        UploadEffectParameters();
        _context.CSSetShaderResources(2, new[] { CurrentField!, _timeHistorySrv! });
        DispatchSortPass(0, 0);
        _context.CSSetShaderResources(2, new ID3D11ShaderResourceView[] { null!, null! });
        if (Effect == ImageSimulationEffect.TimeDisplacement) _historyHead = (_historyHead + 1) % HistoryCapacity;
    }

    private void AdvanceField(uint pass)
    {
        UploadEffectParameters(pass);
        _context!.CSSetShader(_advanceFieldShader);
        _context.CSSetShaderResources(1, new[] { _snapshotSrv!, CurrentField! });
        _context.CSSetUnorderedAccessViews(1, new[] { (_fieldAIsSource ? _fieldUavB : _fieldUavA)! });
        DispatchGrid(_context, _fieldWidth, _fieldHeight);
        _context.CSSetUnorderedAccessViews(1, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(1, new ID3D11ShaderResourceView[] { null!, null! });
        _fieldAIsSource = !_fieldAIsSource;
        _fieldReady = true;
    }

    private void DisposeFieldTextures()
    {
        _fieldSrvA?.Dispose(); _fieldSrvB?.Dispose(); _fieldUavA?.Dispose(); _fieldUavB?.Dispose();
        _fieldA?.Dispose(); _fieldB?.Dispose(); _timeHistorySrv?.Dispose(); _timeHistoryUav?.Dispose(); _timeHistory?.Dispose();
        _fieldSrvA = _fieldSrvB = null; _fieldUavA = _fieldUavB = null; _fieldA = _fieldB = null;
        _timeHistorySrv = null; _timeHistoryUav = null; _timeHistory = null;
        _fieldReady = false; _historyCount = 0;
    }
}
