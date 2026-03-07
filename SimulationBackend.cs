using System;
using System.Collections.Generic;

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
    IReadOnlyList<bool[,]> Frames { get; }

    void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null);
    void SetBinningMode(GameOfLifeEngine.BinningMode mode);
    void SetInjectionMode(GameOfLifeEngine.InjectionMode mode);
    void SetMode(GameOfLifeEngine.LifeMode mode);
    void Randomize();
    void Step();
    (byte r, byte g, byte b) GetColor(int row, int col);
    void FillColorBuffer(byte[] targetBuffer);
    void InjectFrame(bool[,] frame);
    void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue);
}

internal sealed class CpuSimulationBackend : ISimulationBackend
{
    private readonly GameOfLifeEngine _engine = new();

    public int Columns => _engine.Columns;
    public int Rows => _engine.Rows;
    public int Depth => _engine.Depth;
    public double AspectRatio => _engine.AspectRatio;
    public GameOfLifeEngine.LifeMode Mode => _engine.Mode;
    public GameOfLifeEngine.BinningMode BinMode => _engine.BinMode;
    public int RDepth => _engine.RDepth;
    public int GDepth => _engine.GDepth;
    public int BDepth => _engine.BDepth;
    public GameOfLifeEngine.InjectionMode InjectMode => _engine.InjectMode;
    public IReadOnlyList<bool[,]> Frames => _engine.Frames;

    public void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null) => _engine.Configure(requestedRows, requestedDepth, aspectRatio);

    public void SetBinningMode(GameOfLifeEngine.BinningMode mode) => _engine.SetBinningMode(mode);

    public void SetInjectionMode(GameOfLifeEngine.InjectionMode mode) => _engine.SetInjectionMode(mode);

    public void SetMode(GameOfLifeEngine.LifeMode mode) => _engine.SetMode(mode);

    public void Randomize() => _engine.Randomize();

    public void Step() => _engine.Step();

    public (byte r, byte g, byte b) GetColor(int row, int col) => _engine.GetColor(row, col);

    public void FillColorBuffer(byte[] targetBuffer)
        => _engine.FillColorBuffer(targetBuffer);

    public void InjectFrame(bool[,] frame) => _engine.InjectFrame(frame);

    public void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue) => _engine.InjectRgbFrame(red, green, blue);

    public void Dispose()
    {
        // CPU backend owns no unmanaged resources.
    }
}
