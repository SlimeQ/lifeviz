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

    public void FillColorBuffer(byte[] targetBuffer)
    {
        EnsureInitialized();

        int expectedLength = Columns * Rows * 4;
        if (targetBuffer.Length < expectedLength)
        {
            throw new ArgumentException("Target buffer is smaller than simulation output.", nameof(targetBuffer));
        }

        if (_mode == LifeMode.NaiveGrayscale)
        {
            var (rSlice, gSlice, bSlice) = CalculateSlices();
            int rFrames = Math.Min(rSlice.length, _history.Count - rSlice.start);
            int gFrames = Math.Min(gSlice.length, _history.Count - gSlice.start);
            int bFrames = Math.Min(bSlice.length, _history.Count - bSlice.start);

            Parallel.For(0, Rows, row =>
            {
                int rowOffset = row * Columns * 4;
                for (int col = 0; col < Columns; col++)
                {
                    byte r = EvaluateSliceFast(row, col, _history, rSlice.start, rFrames);
                    byte g = EvaluateSliceFast(row, col, _history, gSlice.start, gFrames);
                    byte b = EvaluateSliceFast(row, col, _history, bSlice.start, bFrames);
                    int index = rowOffset + (col * 4);
                    targetBuffer[index] = r;
                    targetBuffer[index + 1] = g;
                    targetBuffer[index + 2] = b;
                    targetBuffer[index + 3] = 255;
                }
            });
            return;
        }

        int rFramesRgb = Math.Min(_rDepth, _historyR.Count);
        int gFramesRgb = Math.Min(_gDepth, _historyG.Count);
        int bFramesRgb = Math.Min(_bDepth, _historyB.Count);
        Parallel.For(0, Rows, row =>
        {
            int rowOffset = row * Columns * 4;
            for (int col = 0; col < Columns; col++)
            {
                byte r = EvaluateChannelFast(row, col, _historyR, rFramesRgb);
                byte g = EvaluateChannelFast(row, col, _historyG, gFramesRgb);
                byte b = EvaluateChannelFast(row, col, _historyB, bFramesRgb);
                int index = rowOffset + (col * 4);
                targetBuffer[index] = r;
                targetBuffer[index + 1] = g;
                targetBuffer[index + 2] = b;
                targetBuffer[index + 3] = 255;
            }
        });
    }

    public void InjectFrame(bool[,] frame)
    {
        if (!ValidateFrame(frame))
        {
            return;
        }

        EnsureInitialized();

        if (_mode == LifeMode.NaiveGrayscale)
        {
            var current = EnsureTopFrame(_history, Rows, Columns);
            ApplyMask(current, frame);
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

        EnsureInitialized();

        var currentR = EnsureTopFrame(_historyR, Rows, Columns);
        var currentG = EnsureTopFrame(_historyG, Rows, Columns);
        var currentB = EnsureTopFrame(_historyB, Rows, Columns);

        ApplyMask(currentR, red);
        ApplyMask(currentG, green);
        ApplyMask(currentB, blue);
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

    private int CountNeighborsEdge(bool[,] frame, int row, int col)
    {
        int count = 0;
        int rowStart = Math.Max(0, row - 1);
        int rowEnd = Math.Min(Rows - 1, row + 1);
        int colStart = Math.Max(0, col - 1);
        int colEnd = Math.Min(Columns - 1, col + 1);
        for (int nr = rowStart; nr <= rowEnd; nr++)
        {
            for (int nc = colStart; nc <= colEnd; nc++)
            {
                if ((nr != row || nc != col) && frame[nr, nc])
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

    private static bool[,] EnsureTopFrame(List<bool[,]> history, int rows, int cols)
    {
        if (history.Count == 0)
        {
            var frame = new bool[rows, cols];
            history.Add(frame);
            return frame;
        }

        return history[0];
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
        bool[,] next;
        if (history.Count >= depth && history[^1].GetLength(0) == Rows && history[^1].GetLength(1) == Columns)
        {
            next = history[^1];
            Array.Clear(next);
        }
        else
        {
            next = CreateFrame();
        }

        Parallel.For(0, Rows, row =>
        {
            if (Columns == 1 || row == 0 || row == Rows - 1)
            {
                for (int col = 0; col < Columns; col++)
                {
                    int neighbors = CountNeighborsEdge(current, row, col);
                    bool alive = current[row, col];
                    next[row, col] = neighbors == 3 || (alive && neighbors == 2);
                }
                return;
            }

            int lastCol = Columns - 1;
            int prev = row - 1;
            int nextRow = row + 1;

            {
                int neighbors = CountNeighborsEdge(current, row, 0);
                bool alive = current[row, 0];
                next[row, 0] = neighbors == 3 || (alive && neighbors == 2);
            }

            for (int col = 1; col < lastCol; col++)
            {
                int left = col - 1;
                int right = col + 1;
                int neighbors =
                    (current[prev, left] ? 1 : 0) +
                    (current[prev, col] ? 1 : 0) +
                    (current[prev, right] ? 1 : 0) +
                    (current[row, left] ? 1 : 0) +
                    (current[row, right] ? 1 : 0) +
                    (current[nextRow, left] ? 1 : 0) +
                    (current[nextRow, col] ? 1 : 0) +
                    (current[nextRow, right] ? 1 : 0);

                bool alive = current[row, col];
                next[row, col] = neighbors == 3 || (alive && neighbors == 2);
            }

            if (lastCol > 0)
            {
                int neighbors = CountNeighborsEdge(current, row, lastCol);
                bool alive = current[row, lastCol];
                next[row, lastCol] = neighbors == 3 || (alive && neighbors == 2);
            }
        });

        if (history.Count >= depth && ReferenceEquals(history[^1], next))
        {
            history.RemoveAt(history.Count - 1);
            history.Insert(0, next);
        }
        else
        {
            history.Insert(0, next);
            TrimChannel(history, depth);
        }
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

    private byte EvaluateSliceFast(int row, int col, List<bool[,]> history, int sliceStart, int frames)
    {
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
                if (historyIndex >= history.Count)
                {
                    break;
                }

                if (history[historyIndex][row, col])
                {
                    value |= 1L << (frames - 1 - i);
                }
            }

            long max = (1L << frames) - 1;
            return max <= 0 ? (byte)0 : (byte)Math.Round((value / (double)max) * 255);
        }

        int alive = 0;
        int considered = 0;
        for (int i = 0; i < frames; i++)
        {
            int historyIndex = sliceStart + i;
            if (historyIndex >= history.Count)
            {
                break;
            }

            considered++;
            if (history[historyIndex][row, col])
            {
                alive++;
            }
        }

        return considered <= 0 ? (byte)0 : (byte)Math.Round((alive / (double)considered) * 255);
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

    private byte EvaluateChannelFast(int row, int col, List<bool[,]> history, int frames)
    {
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
                    value |= 1L << (frames - 1 - i);
                }
            }

            long max = (1L << frames) - 1;
            return max <= 0 ? (byte)0 : (byte)Math.Round((value / (double)max) * 255);
        }

        int alive = 0;
        for (int i = 0; i < frames; i++)
        {
            if (history[i][row, col])
            {
                alive++;
            }
        }

        return (byte)Math.Round((alive / (double)frames) * 255);
    }

    private bool ValidateFrame(bool[,] frame) => frame.GetLength(0) == Rows && frame.GetLength(1) == Columns;

    private (int r, int g, int b) CalculateChannelDepths()
    {
        var (r, g, b) = CalculateSlices();
        return (r.length, g.length, b.length);
    }
}
