using System;
using System.Collections.Generic;

namespace lifeviz;

internal sealed class GameOfLifeEngine
{
    private const int MinColumns = 32;
    private const int MaxColumns = 512;
    private const int MinDepth = 3;
    private const int MaxDepth = 96;
    private const double DefaultAspectRatio = 16d / 9d;
    private readonly Random _random = new();
    private readonly List<bool[,]> _history = new();
    private double _aspectRatio = DefaultAspectRatio;

    public int Columns { get; private set; } = 128;
    public int Rows { get; private set; } = 72;
    public int Depth { get; private set; } = 24;
    public double AspectRatio => _aspectRatio;

    public IReadOnlyList<bool[,]> Frames => _history;

    public void Configure(int requestedColumns, int requestedDepth, double? aspectRatio = null)
    {
        if (aspectRatio.HasValue && aspectRatio.Value > 0.01)
        {
            _aspectRatio = aspectRatio.Value;
        }

        Columns = Math.Clamp(requestedColumns, MinColumns, MaxColumns);
        Depth = Math.Clamp(requestedDepth, MinDepth, MaxDepth);
        Rows = Math.Max(9, (int)Math.Round(Columns / _aspectRatio));

        if (Rows < 3)
        {
            Rows = 3;
        }

        _history.Clear();
        for (int i = 0; i < Depth; i++)
        {
            _history.Add(CreateFrame());
        }

        Randomize();
    }

    public void Randomize()
    {
        EnsureInitialized();

        foreach (var frame in _history)
        {
            FillRandom(frame);
        }
    }

    public void Step()
    {
        EnsureInitialized();

        var current = _history[0];
        var next = CreateFrame();

        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int neighbors = CountNeighbors(current, row, col);
                bool alive = current[row, col];
                next[row, col] = neighbors == 3 || (alive && neighbors == 2);
            }
        }

        _history.Insert(0, next);
        if (_history.Count > Depth)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    public (byte r, byte g, byte b) GetColor(int row, int col)
    {
        EnsureInitialized();

        if (Depth <= 0)
        {
            return (0, 0, 0);
        }

        var (rSlice, gSlice, bSlice) = CalculateSlices();

        byte r = EvaluateSlice(row, col, rSlice.start, rSlice.length);
        byte g = EvaluateSlice(row, col, gSlice.start, gSlice.length);
        byte b = EvaluateSlice(row, col, bSlice.start, bSlice.length);

        return (r, g, b);
    }

    private void EnsureInitialized()
    {
        if (_history.Count > 0)
        {
            return;
        }

        Configure(Columns, Depth, _aspectRatio);
    }

    private bool[,] CreateFrame() => new bool[Rows, Columns];

    private void FillRandom(bool[,] frame)
    {
        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                frame[row, col] = _random.NextDouble() < 0.35;
            }
        }
    }

    private int CountNeighbors(bool[,] frame, int row, int col)
    {
        int count = 0;
        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0)
                {
                    continue;
                }

                int nr = row + dr;
                int nc = col + dc;
                if (nr >= 0 && nr < Rows && nc >= 0 && nc < Columns && frame[nr, nc])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private ((int start, int length) start, (int start, int length) middle, (int start, int length) end) CalculateSlices()
    {
        int baseSlice = Depth / 3;
        int remainder = Depth % 3;

        int rLen = baseSlice + (remainder > 0 ? 1 : 0);
        int gLen = baseSlice + (remainder > 1 ? 1 : 0);
        int bLen = Depth - rLen - gLen;

        var r = (start: 0, length: rLen);
        var g = (start: r.start + r.length, length: gLen);
        var b = (start: g.start + g.length, length: bLen);

        return (r, g, b);
    }

    public void InjectFrame(bool[,] frame)
    {
        if (frame.GetLength(0) != Rows || frame.GetLength(1) != Columns)
        {
            return;
        }

        _history.Insert(0, frame);
        if (_history.Count > Depth)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private byte EvaluateSlice(int row, int col, int sliceStart, int sliceLength)
    {
        if (sliceLength <= 0)
        {
            return 0;
        }

        long value = 0;
        for (int i = 0; i < sliceLength; i++)
        {
            int historyIndex = sliceStart + i;
            if (historyIndex >= _history.Count)
            {
                break;
            }

            if (_history[historyIndex][row, col])
            {
                int bit = sliceLength - 1 - i;
                value |= 1L << bit;
            }
        }

        long max = (1L << sliceLength) - 1;
        if (max <= 0)
        {
            return 0;
        }

        double normalized = value / (double)max;
        return (byte)Math.Round(normalized * 255);
    }
}
