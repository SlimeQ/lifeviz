using System;
namespace lifeviz;

internal interface ISimulationBackend : IDisposable
{
    int Columns { get; }
    int Rows { get; }
    int Depth { get; }
    double AspectRatio { get; }
    GameOfLifeEngine.LifeMode Mode { get; }
    GameOfLifeEngine.BinningMode BinMode { get; }
    int RDepth { get; }
    int GDepth { get; }
    int BDepth { get; }
    GameOfLifeEngine.InjectionMode InjectMode { get; }

    void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null);
    void SetBinningMode(GameOfLifeEngine.BinningMode mode);
    void SetInjectionMode(GameOfLifeEngine.InjectionMode mode);
    void SetMode(GameOfLifeEngine.LifeMode mode);
    void Randomize();
    void Step();
    void FillColorBuffer(byte[] targetBuffer);
    void InjectFrame(bool[,] frame);
    void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue);
}

internal interface IGpuSimulationSurfaceBackend : ISimulationBackend
{
    bool TryInjectCompositeSurface(
        GpuCompositeSurface? compositeSurface,
        double min,
        double max,
        bool invertThreshold,
        GameOfLifeEngine.InjectionMode mode,
        double noiseProbability,
        int period,
        int pulseStep,
        bool invertInput,
        double hueShiftDegrees = 0.0);

    bool TryGetSharedColorTexture(out IntPtr sharedHandle, out int width, out int height);

    bool TryGetColorSurface(out GpuCompositeSurface? surface);
}
