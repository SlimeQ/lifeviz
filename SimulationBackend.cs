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
