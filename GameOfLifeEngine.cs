using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace lifeviz;

    internal sealed class GameOfLifeEngine
    {
        internal enum LifeMode
        {
            NaiveGrayscale,
            RgbChannels
        }

    internal enum BinningMode
    {
        Fill,
        Binary
    }

    internal enum InjectionMode
    {
        Threshold,
        RandomPulse,
        PulseWidthModulation
    }

        private const int MinRows = 72;
        private const int MaxRows = 2160;
        private const int MinColumns = 32;
        private const int MaxColumns = 4096;
        private const int MinDepth = 3;
        private const int MaxDepth = 96;
        private const double DefaultAspectRatio = 16d / 9d;
        private readonly Random _random = new();
        private readonly List<bool[,]> _history = new();
        private readonly List<bool[,]> _historyR = new();
        private readonly List<bool[,]> _historyG = new();
        private readonly List<bool[,]> _historyB = new();
        private double _aspectRatio = DefaultAspectRatio;
    private LifeMode _mode = LifeMode.NaiveGrayscale;
    private BinningMode _binningMode = BinningMode.Fill;
    private InjectionMode _injectionMode = InjectionMode.Threshold;
        private int _rDepth;
        private int _gDepth;
        private int _bDepth;

    public int Columns { get; private set; } = 256;
    public int Rows { get; private set; } = 144;
    public int Depth { get; private set; } = 24;
    public double AspectRatio => _aspectRatio;
    public LifeMode Mode => _mode;
    public BinningMode BinMode => _binningMode;
    public int RDepth => _rDepth;
    public int GDepth => _gDepth;
    public int BDepth => _bDepth;
    public InjectionMode InjectMode => _injectionMode;

    public IReadOnlyList<bool[,]> Frames => _history;

    public void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null)
    {
        if (aspectRatio.HasValue && aspectRatio.Value > 0.01)
        {
            _aspectRatio = aspectRatio.Value;
        }

        Rows = Math.Clamp(requestedRows, MinRows, MaxRows);
        Depth = Math.Clamp(requestedDepth, MinDepth, MaxDepth);
        Columns = (int)Math.Round(Rows * _aspectRatio);
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns);
        Rows = Math.Max(9, (int)Math.Round(Columns / _aspectRatio));
        Rows = Math.Clamp(Rows, MinRows, MaxRows);

        if (Rows < 3)
        {
            Rows = 3;
        }

        (_rDepth, _gDepth, _bDepth) = CalculateChannelDepths();
        ResetHistories();
    }

    public void SetBinningMode(BinningMode mode)
    {
        _binningMode = mode;
    }

    public void SetInjectionMode(InjectionMode mode)
    {
        _injectionMode = mode;
    }

    public void SetMode(LifeMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        ResetHistories();
        Randomize();
    }

    public void Randomize()
    {
        EnsureInitialized();

        if (_mode == LifeMode.NaiveGrayscale)
        {
            foreach (var frame in _history)
            {
                FillRandom(frame);
            }
        }
        else
        {
            RandomizeChannel(_historyR);
            RandomizeChannel(_historyG);
            RandomizeChannel(_historyB);
        }
    }

    public void Step()
    {
        EnsureInitialized();

        if (_mode == LifeMode.NaiveGrayscale)
        {
            StepChannel(_history, Depth);
        }
        else
        {
            StepChannel(_historyR, _rDepth);
            StepChannel(_historyG, _gDepth);
            StepChannel(_historyB, _bDepth);
        }
    }

    public (byte r, byte g, byte b) GetColor(int row, int col)
    {
        EnsureInitialized();

        if (Depth <= 0)
        {
            return (0, 0, 0);
        }

        if (_mode == LifeMode.NaiveGrayscale)
        {
            var (rSlice, gSlice, bSlice) = CalculateSlices();

            byte r = EvaluateSlice(row, col, rSlice.start, rSlice.length);
            byte g = EvaluateSlice(row, col, gSlice.start, gSlice.length);
            byte b = EvaluateSlice(row, col, bSlice.start, bSlice.length);

            return (r, g, b);
        }

        byte rChannel = EvaluateChannel(row, col, _historyR, _rDepth);
        byte gChannel = EvaluateChannel(row, col, _historyG, _gDepth);
        byte bChannel = EvaluateChannel(row, col, _historyB, _bDepth);
        return (rChannel, gChannel, bChannel);
    }

    public void InjectFrame(bool[,] frame)
    {
        if (!ValidateFrame(frame))
        {
            return;
        }

        if (_mode == LifeMode.NaiveGrayscale)
        {
            var next = CloneTopOrEmpty(_history, Rows, Columns);
            ApplyMask(next, frame);
            _history.Insert(0, next);
            TrimChannel(_history, Depth);
        }
        else
        {
            // Duplicate grayscale into all channels when in RGB mode without per-channel data.
            InjectRgbFrame(frame, frame, frame);
        }
    }

    public void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue)
    {
        if (_mode != LifeMode.RgbChannels)
        {
            return;
        }

        if (!ValidateFrame(red) || !ValidateFrame(green) || !ValidateFrame(blue))
        {
            return;
        }

        var nextR = CloneTopOrEmpty(_historyR, Rows, Columns);
        var nextG = CloneTopOrEmpty(_historyG, Rows, Columns);
        var nextB = CloneTopOrEmpty(_historyB, Rows, Columns);

        ApplyMask(nextR, red);
        ApplyMask(nextG, green);
        ApplyMask(nextB, blue);

        _historyR.Insert(0, nextR);
        _historyG.Insert(0, nextG);
        _historyB.Insert(0, nextB);

        TrimChannel(_historyR, _rDepth);
        TrimChannel(_historyG, _gDepth);
        TrimChannel(_historyB, _bDepth);
    }

    private void EnsureInitialized()
    {
        if (_mode == LifeMode.NaiveGrayscale && _history.Count > 0)
        {
            return;
        }

        if (_mode == LifeMode.RgbChannels && (_historyR.Count > 0 || _historyG.Count > 0 || _historyB.Count > 0))
        {
            return;
        }

        Configure(Rows, Depth, _aspectRatio);
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

    private void ResetHistories()
    {
        _history.Clear();
        _historyR.Clear();
        _historyG.Clear();
        _historyB.Clear();

        if (_mode == LifeMode.NaiveGrayscale)
        {
            for (int i = 0; i < Depth; i++)
            {
                _history.Add(CreateFrame());
            }
        }
        else
        {
            for (int i = 0; i < _rDepth; i++)
            {
                _historyR.Add(CreateFrame());
            }
            for (int i = 0; i < _gDepth; i++)
            {
                _historyG.Add(CreateFrame());
            }
            for (int i = 0; i < _bDepth; i++)
            {
                _historyB.Add(CreateFrame());
            }
        }
    }

    private void RandomizeChannel(List<bool[,]> history)
    {
        foreach (var frame in history)
        {
            FillRandom(frame);
        }
    }

    private static bool[,] CloneTopOrEmpty(List<bool[,]> history, int rows, int cols)
    {
        if (history.Count == 0)
        {
            return new bool[rows, cols];
        }

        var source = history[0];
        var clone = new bool[rows, cols];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private void ApplyMask(bool[,] target, bool[,] mask)
    {
        int rows = target.GetLength(0);
        int cols = target.GetLength(1);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (mask[r, c] && ShouldInject(mask[r, c], target[r, c]))
                {
                    target[r, c] = true;
                }
            }
        }
    }

    private bool ShouldInject(bool maskValue, bool existingValue)
    {
        if (!maskValue)
        {
            return false;
        }

        return true;
    }

    private void StepChannel(List<bool[,]> history, int depth)
    {
        if (history.Count == 0)
        {
            history.Add(CreateFrame());
        }

        var current = history[0];
        var next = CreateFrame();

        Parallel.For(0, Rows, row =>
        {
            for (int col = 0; col < Columns; col++)
            {
                int neighbors = CountNeighbors(current, row, col);
                bool alive = current[row, col];
                next[row, col] = neighbors == 3 || (alive && neighbors == 2);
            }
        });

        history.Insert(0, next);
        TrimChannel(history, depth);
    }

    private void TrimChannel(List<bool[,]> history, int depth)
    {
        if (history.Count > depth)
        {
            history.RemoveAt(history.Count - 1);
        }
    }

    private byte EvaluateSlice(int row, int col, int sliceStart, int sliceLength)
    {
        if (sliceLength <= 0)
        {
            return 0;
        }

        int frames = Math.Min(sliceLength, _history.Count);
        if (frames <= 0)
        {
            return 0;
        }

        if (_binningMode == BinningMode.Binary)
        {
            long value = 0;
            for (int i = 0; i < frames; i++)
            {
                int historyIndex = sliceStart + i;
                if (historyIndex >= _history.Count)
                {
                    break;
                }

                if (_history[historyIndex][row, col])
                {
                    int bit = frames - 1 - i;
                    value |= 1L << bit;
                }
            }

            long max = (1L << frames) - 1;
            if (max <= 0)
            {
                return 0;
            }

            double normalized = value / (double)max;
            return (byte)Math.Round(normalized * 255);
        }
        else
        {
            int alive = 0;
            int considered = 0;
            for (int i = 0; i < frames; i++)
            {
                int historyIndex = sliceStart + i;
                if (historyIndex >= _history.Count)
                {
                    break;
                }

                considered++;
                if (_history[historyIndex][row, col])
                {
                    alive++;
                }
            }

            if (considered == 0)
            {
                return 0;
            }

            double normalized = alive / (double)considered;
            return (byte)Math.Round(normalized * 255);
        }
    }

    private byte EvaluateChannel(int row, int col, List<bool[,]> history, int channelDepth)
    {
        if (channelDepth <= 0 || history.Count == 0)
        {
            return 0;
        }

        int frames = Math.Min(channelDepth, history.Count);
        if (frames <= 0)
        {
            return 0;
        }

        if (_binningMode == BinningMode.Binary)
        {
            long value = 0;
            for (int i = 0; i < frames; i++)
            {
                if (history[i][row, col])
                {
                    int bit = frames - 1 - i;
                    value |= 1L << bit;
                }
            }

            long max = (1L << frames) - 1;
            if (max <= 0)
            {
                return 0;
            }

            double normalized = value / (double)max;
            return (byte)Math.Round(normalized * 255);
        }
        else
        {
            int alive = 0;
            for (int i = 0; i < frames; i++)
            {
                if (history[i][row, col])
                {
                    alive++;
                }
            }

            double normalized = alive / (double)frames;
            return (byte)Math.Round(normalized * 255);
        }
    }

    private bool ValidateFrame(bool[,] frame) => frame.GetLength(0) == Rows && frame.GetLength(1) == Columns;

    private (int r, int g, int b) CalculateChannelDepths()
    {
        var (r, g, b) = CalculateSlices();
        return (r.length, g.length, b.length);
    }
}
