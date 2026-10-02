using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace lifeviz;

internal enum FitMode
{
    Fit,
    Fill,
    Stretch,
    Center,
    Tile,
    Span
}

internal readonly struct FitMapping
{
    public FitMapping(FitMode mode, int sourceWidth, int sourceHeight, int destWidth, int destHeight, double scaleX, double scaleY, double offsetX, double offsetY, double scaledWidth, double scaledHeight)
    {
        Mode = mode;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        DestWidth = destWidth;
        DestHeight = destHeight;
        ScaleX = scaleX;
        ScaleY = scaleY;
        OffsetX = offsetX;
        OffsetY = offsetY;
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
    }

    public FitMode Mode { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int DestWidth { get; }
    public int DestHeight { get; }
    public double ScaleX { get; }
    public double ScaleY { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double ScaledWidth { get; }
    public double ScaledHeight { get; }
}

internal static class ImageFit
{
    public static FitMode Normalize(FitMode mode) => mode == FitMode.Span ? FitMode.Fill : mode;

    public static FitMapping GetMapping(FitMode mode, int sourceWidth, int sourceHeight, int destWidth, int destHeight)
    {
        mode = Normalize(mode);
        if (sourceWidth <= 0 || sourceHeight <= 0 || destWidth <= 0 || destHeight <= 0)
        {
            return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, 1.0, 1.0, 0.0, 0.0, destWidth, destHeight);
        }

        switch (mode)
        {
            case FitMode.Fit:
            {
                double scale = Math.Min(destWidth / (double)sourceWidth, destHeight / (double)sourceHeight);
                scale = NormalizeScale(scale);
                double scaledWidth = sourceWidth * scale;
                double scaledHeight = sourceHeight * scale;
                double offsetX = (destWidth - scaledWidth) / 2.0;
                double offsetY = (destHeight - scaledHeight) / 2.0;
                return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, scale, scale, offsetX, offsetY, scaledWidth, scaledHeight);
            }
            case FitMode.Fill:
            {
                double scale = Math.Max(destWidth / (double)sourceWidth, destHeight / (double)sourceHeight);
                scale = NormalizeScale(scale);
                double scaledWidth = sourceWidth * scale;
                double scaledHeight = sourceHeight * scale;
                double offsetX = (scaledWidth - destWidth) / 2.0;
                double offsetY = (scaledHeight - destHeight) / 2.0;
                return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, scale, scale, offsetX, offsetY, scaledWidth, scaledHeight);
            }
            case FitMode.Center:
            {
                double offsetX = (destWidth - sourceWidth) / 2.0;
                double offsetY = (destHeight - sourceHeight) / 2.0;
                return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, 1.0, 1.0, offsetX, offsetY, sourceWidth, sourceHeight);
            }
            case FitMode.Tile:
                return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, 1.0, 1.0, 0.0, 0.0, sourceWidth, sourceHeight);
            case FitMode.Stretch:
            default:
            {
                double scaleX = sourceWidth / (double)destWidth;
                double scaleY = sourceHeight / (double)destHeight;
                scaleX = NormalizeScale(scaleX);
                scaleY = NormalizeScale(scaleY);
                return new FitMapping(mode, sourceWidth, sourceHeight, destWidth, destHeight, scaleX, scaleY, 0.0, 0.0, destWidth, destHeight);
            }
        }
    }

    public static bool TryMapPixel(FitMapping mapping, int col, int row, out int srcX, out int srcY)
    {
        srcX = 0;
        srcY = 0;

        switch (mapping.Mode)
        {
            case FitMode.Fit:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.ScaledWidth || yIn >= mapping.ScaledHeight)
                {
                    return false;
                }

                srcX = ClampToInt((int)Math.Floor(xIn / mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn / mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Fill:
            {
                double xIn = col + mapping.OffsetX;
                double yIn = row + mapping.OffsetY;
                srcX = ClampToInt((int)Math.Floor(xIn / mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn / mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Center:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.SourceWidth || yIn >= mapping.SourceHeight)
                {
                    return false;
                }

                srcX = ClampToInt((int)Math.Floor(xIn), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Tile:
                srcX = PositiveMod(col, mapping.SourceWidth);
                srcY = PositiveMod(row, mapping.SourceHeight);
                return true;
            case FitMode.Stretch:
            default:
                srcX = ClampToInt((int)Math.Floor(col * mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(row * mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
        }
    }

    public static bool TryMapPixel(FitMapping mapping, double col, double row, out int srcX, out int srcY)
    {
        srcX = 0;
        srcY = 0;

        switch (mapping.Mode)
        {
            case FitMode.Fit:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.ScaledWidth || yIn >= mapping.ScaledHeight)
                {
                    return false;
                }

                srcX = ClampToInt((int)Math.Floor(xIn / mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn / mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Fill:
            {
                double xIn = col + mapping.OffsetX;
                double yIn = row + mapping.OffsetY;
                srcX = ClampToInt((int)Math.Floor(xIn / mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn / mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Center:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.SourceWidth || yIn >= mapping.SourceHeight)
                {
                    return false;
                }

                srcX = ClampToInt((int)Math.Floor(xIn), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(yIn), 0, mapping.SourceHeight - 1);
                return true;
            }
            case FitMode.Tile:
                srcX = PositiveMod((int)Math.Floor(col), mapping.SourceWidth);
                srcY = PositiveMod((int)Math.Floor(row), mapping.SourceHeight);
                return true;
            case FitMode.Stretch:
            default:
                srcX = ClampToInt((int)Math.Floor(col * mapping.ScaleX), 0, mapping.SourceWidth - 1);
                srcY = ClampToInt((int)Math.Floor(row * mapping.ScaleY), 0, mapping.SourceHeight - 1);
                return true;
        }
    }

    public static bool TryMapSamplePoint(FitMapping mapping, double col, double row, out double srcX, out double srcY)
    {
        srcX = 0;
        srcY = 0;

        switch (mapping.Mode)
        {
            case FitMode.Fit:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.ScaledWidth || yIn >= mapping.ScaledHeight)
                {
                    return false;
                }

                srcX = (xIn / mapping.ScaleX) - 0.5;
                srcY = (yIn / mapping.ScaleY) - 0.5;
                return true;
            }
            case FitMode.Fill:
            {
                double xIn = col + mapping.OffsetX;
                double yIn = row + mapping.OffsetY;
                srcX = (xIn / mapping.ScaleX) - 0.5;
                srcY = (yIn / mapping.ScaleY) - 0.5;
                return true;
            }
            case FitMode.Center:
            {
                double xIn = col - mapping.OffsetX;
                double yIn = row - mapping.OffsetY;
                if (xIn < 0 || yIn < 0 || xIn >= mapping.SourceWidth || yIn >= mapping.SourceHeight)
                {
                    return false;
                }

                srcX = xIn - 0.5;
                srcY = yIn - 0.5;
                return true;
            }
            case FitMode.Tile:
                srcX = PositiveMod(col, mapping.SourceWidth) - 0.5;
                srcY = PositiveMod(row, mapping.SourceHeight) - 0.5;
                return true;
            case FitMode.Stretch:
            default:
                srcX = (col * mapping.ScaleX) - 0.5;
                srcY = (row * mapping.ScaleY) - 0.5;
                return true;
        }
    }

    public static void SampleBgraBilinear(byte[] source, int sourceWidth, int sourceHeight, double srcX, double srcY,
        out byte b, out byte g, out byte r, out byte a)
    {
        b = 0;
        g = 0;
        r = 0;
        a = 0;

        if (sourceWidth <= 0 || sourceHeight <= 0 || source.Length < sourceWidth * sourceHeight * 4)
        {
            return;
        }

        double clampedX = Math.Clamp(srcX, 0, sourceWidth - 1);
        double clampedY = Math.Clamp(srcY, 0, sourceHeight - 1);

        int x0 = ClampToInt((int)Math.Floor(clampedX), 0, sourceWidth - 1);
        int y0 = ClampToInt((int)Math.Floor(clampedY), 0, sourceHeight - 1);
        int x1 = ClampToInt(x0 + 1, 0, sourceWidth - 1);
        int y1 = ClampToInt(y0 + 1, 0, sourceHeight - 1);

        double fx = Math.Clamp(clampedX - x0, 0, 1);
        double fy = Math.Clamp(clampedY - y0, 0, 1);

        int stride = sourceWidth * 4;
        int i00 = (y0 * stride) + (x0 * 4);
        int i10 = (y0 * stride) + (x1 * 4);
        int i01 = (y1 * stride) + (x0 * 4);
        int i11 = (y1 * stride) + (x1 * 4);

        static double Lerp(double p00, double p10, double p01, double p11, double fxLocal, double fyLocal)
        {
            double top = p00 + ((p10 - p00) * fxLocal);
            double bottom = p01 + ((p11 - p01) * fxLocal);
            return top + ((bottom - top) * fyLocal);
        }

        b = (byte)Math.Round(Lerp(source[i00], source[i10], source[i01], source[i11], fx, fy));
        g = (byte)Math.Round(Lerp(source[i00 + 1], source[i10 + 1], source[i01 + 1], source[i11 + 1], fx, fy));
        r = (byte)Math.Round(Lerp(source[i00 + 2], source[i10 + 2], source[i01 + 2], source[i11 + 2], fx, fy));
        a = (byte)Math.Round(Lerp(source[i00 + 3], source[i10 + 3], source[i01 + 3], source[i11 + 3], fx, fy));
    }

    public static bool TrySampleMappedBgra(byte[] source, int sourceWidth, int sourceHeight, FitMapping mapping,
        double destX, double destY, bool mirror, out byte b, out byte g, out byte r, out byte a)
    {
        b = 0;
        g = 0;
        r = 0;
        a = 0;

        if (!TryMapSamplePoint(mapping, destX, destY, out double sampleX, out double sampleY))
        {
            return false;
        }

        if (mirror)
        {
            sampleX = (sourceWidth - 1) - sampleX;
        }

        SampleBgraBilinear(source, sourceWidth, sourceHeight, sampleX, sampleY, out b, out g, out r, out a);
        return true;
    }

    public static bool TrySampleMappedBgraSupersampled(byte[] source, int sourceWidth, int sourceHeight, FitMapping mapping,
        double destCenterX, double destCenterY, bool mirror, out byte b, out byte g, out byte r, out byte a)
    {
        const int GridSize = 2;
        const double MinOffset = -0.25;
        const double MaxOffset = 0.25;

        double sumB = 0;
        double sumG = 0;
        double sumR = 0;
        double sumA = 0;
        int samples = 0;

        for (int sy = 0; sy < GridSize; sy++)
        {
            double fy = GridSize == 1 ? 0.0 : sy / (double)(GridSize - 1);
            double offsetY = MinOffset + ((MaxOffset - MinOffset) * fy);
            for (int sx = 0; sx < GridSize; sx++)
            {
                double fx = GridSize == 1 ? 0.0 : sx / (double)(GridSize - 1);
                double offsetX = MinOffset + ((MaxOffset - MinOffset) * fx);

                if (!TrySampleMappedBgra(source, sourceWidth, sourceHeight, mapping,
                    destCenterX + offsetX, destCenterY + offsetY, mirror,
                    out byte sampleB, out byte sampleG, out byte sampleR, out byte sampleA))
                {
                    continue;
                }

                sumB += sampleB;
                sumG += sampleG;
                sumR += sampleR;
                sumA += sampleA;
                samples++;
            }
        }

        if (samples == 0)
        {
            b = 0;
            g = 0;
            r = 0;
            a = 0;
            return false;
        }

        b = (byte)Math.Round(sumB / samples);
        g = (byte)Math.Round(sumG / samples);
        r = (byte)Math.Round(sumR / samples);
        a = (byte)Math.Round(sumA / samples);
        return true;
    }

    /// <summary>
    /// Whole-frame equivalent of calling <see cref="TrySampleMappedBgraSupersampled"/> (mirror: false) at every
    /// destination pixel center, producing byte-identical output. The fit mapping is separable, so the 2x2
    /// supersample taps (indices + bilinear weights) are computed once per column and once per row instead of
    /// once per pixel, which removes the per-sample mapping/floor/clamp work that dominated CPU capture paths.
    /// When <paramref name="opaque"/> is true alpha is forced to 255 (unmapped pixels become opaque black);
    /// otherwise sampled alpha is written and unmapped pixels become transparent black.
    /// </summary>
    public static void ResampleBgraSupersampled(byte[] source, int sourceWidth, int sourceHeight, FitMapping mapping,
        byte[] destination, int destWidth, int destHeight, bool opaque, bool parallel)
    {
        if (destWidth <= 0 || destHeight <= 0)
        {
            return;
        }

        if (sourceWidth <= 0 || sourceHeight <= 0 || source.Length < sourceWidth * sourceHeight * 4)
        {
            // Degenerate source: every mapped tap reads black. Keep the reference sampler for exact parity.
            ResampleBgraSupersampledReference(source, sourceWidth, sourceHeight, mapping, destination, destWidth, destHeight, opaque);
            return;
        }

        var columnTaps = ArrayPool<SupersampleTap>.Shared.Rent(destWidth * 2);
        var rowTaps = ArrayPool<SupersampleTap>.Shared.Rent(destHeight * 2);
        try
        {
            for (int col = 0; col < destWidth; col++)
            {
                columnTaps[col * 2] = BuildTap(mapping, horizontal: true, col + 0.5 - 0.25, sourceWidth, 4);
                columnTaps[(col * 2) + 1] = BuildTap(mapping, horizontal: true, col + 0.5 + 0.25, sourceWidth, 4);
            }

            int sourceStride = sourceWidth * 4;
            for (int row = 0; row < destHeight; row++)
            {
                rowTaps[row * 2] = BuildTap(mapping, horizontal: false, row + 0.5 - 0.25, sourceHeight, sourceStride);
                rowTaps[(row * 2) + 1] = BuildTap(mapping, horizontal: false, row + 0.5 + 0.25, sourceHeight, sourceStride);
            }

            void ResampleRow(int row)
            {
                var rowTap0 = rowTaps[row * 2];
                var rowTap1 = rowTaps[(row * 2) + 1];
                int destIndex = row * destWidth * 4;
                if (Avx2.IsSupported)
                {
                    ResampleRowAvx2(source, columnTaps, rowTap0, rowTap1, destination, destIndex, destWidth, opaque);
                    return;
                }

                for (int col = 0; col < destWidth; col++, destIndex += 4)
                {
                    var colTap0 = columnTaps[col * 2];
                    var colTap1 = columnTaps[(col * 2) + 1];
                    double sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    int samples = 0;

                    // Tap order matches the reference sampler (y-major, then x) so double sums round identically.
                    if (rowTap0.Valid)
                    {
                        if (colTap0.Valid) { AccumulateTap(source, rowTap0, colTap0, opaque, ref sumB, ref sumG, ref sumR, ref sumA); samples++; }
                        if (colTap1.Valid) { AccumulateTap(source, rowTap0, colTap1, opaque, ref sumB, ref sumG, ref sumR, ref sumA); samples++; }
                    }

                    if (rowTap1.Valid)
                    {
                        if (colTap0.Valid) { AccumulateTap(source, rowTap1, colTap0, opaque, ref sumB, ref sumG, ref sumR, ref sumA); samples++; }
                        if (colTap1.Valid) { AccumulateTap(source, rowTap1, colTap1, opaque, ref sumB, ref sumG, ref sumR, ref sumA); samples++; }
                    }

                    if (samples == 0)
                    {
                        destination[destIndex] = 0;
                        destination[destIndex + 1] = 0;
                        destination[destIndex + 2] = 0;
                        destination[destIndex + 3] = opaque ? (byte)255 : (byte)0;
                        continue;
                    }

                    destination[destIndex] = (byte)Math.Round(sumB / samples);
                    destination[destIndex + 1] = (byte)Math.Round(sumG / samples);
                    destination[destIndex + 2] = (byte)Math.Round(sumR / samples);
                    destination[destIndex + 3] = opaque ? (byte)255 : (byte)Math.Round(sumA / samples);
                }
            }

            if (parallel)
            {
                Parallel.For(0, destHeight, ResampleRow);
            }
            else
            {
                for (int row = 0; row < destHeight; row++)
                {
                    ResampleRow(row);
                }
            }
        }
        finally
        {
            ArrayPool<SupersampleTap>.Shared.Return(columnTaps);
            ArrayPool<SupersampleTap>.Shared.Return(rowTaps);
        }
    }

    internal static void ResampleBgraSupersampledReference(byte[] source, int sourceWidth, int sourceHeight, FitMapping mapping,
        byte[] destination, int destWidth, int destHeight, bool opaque)
    {
        for (int row = 0; row < destHeight; row++)
        {
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = ((row * destWidth) + col) * 4;
                bool mapped = TrySampleMappedBgraSupersampled(source, sourceWidth, sourceHeight, mapping,
                    col + 0.5, row + 0.5, mirror: false, out byte b, out byte g, out byte r, out byte a);
                destination[destIndex] = b;
                destination[destIndex + 1] = g;
                destination[destIndex + 2] = r;
                destination[destIndex + 3] = opaque ? (byte)255 : mapped ? a : (byte)0;
            }
        }
    }

    private readonly struct SupersampleTap
    {
        public SupersampleTap(int offset0, int offset1, double fraction)
        {
            Valid = true;
            Offset0 = offset0;
            Offset1 = offset1;
            Fraction = fraction;
        }

        public bool Valid { get; }
        public int Offset0 { get; }
        public int Offset1 { get; }
        public double Fraction { get; }
    }

    // Per-axis split of TryMapSamplePoint + SampleBgraBilinear; `scale` turns the texel index into a byte offset.
    private static SupersampleTap BuildTap(FitMapping mapping, bool horizontal, double dest, int sourceExtent, int scale)
    {
        double offset = horizontal ? mapping.OffsetX : mapping.OffsetY;
        double mapScale = horizontal ? mapping.ScaleX : mapping.ScaleY;
        double scaledExtent = horizontal ? mapping.ScaledWidth : mapping.ScaledHeight;
        int mappedExtent = horizontal ? mapping.SourceWidth : mapping.SourceHeight;
        double src;
        switch (mapping.Mode)
        {
            case FitMode.Fit:
            {
                double inside = dest - offset;
                if (inside < 0 || inside >= scaledExtent)
                {
                    return default;
                }

                src = (inside / mapScale) - 0.5;
                break;
            }
            case FitMode.Fill:
                src = ((dest + offset) / mapScale) - 0.5;
                break;
            case FitMode.Center:
            {
                double inside = dest - offset;
                if (inside < 0 || inside >= mappedExtent)
                {
                    return default;
                }

                src = inside - 0.5;
                break;
            }
            case FitMode.Tile:
                src = PositiveMod(dest, mappedExtent) - 0.5;
                break;
            case FitMode.Stretch:
            default:
                src = (dest * mapScale) - 0.5;
                break;
        }

        double clamped = Math.Clamp(src, 0, sourceExtent - 1);
        int i0 = ClampToInt((int)Math.Floor(clamped), 0, sourceExtent - 1);
        int i1 = ClampToInt(i0 + 1, 0, sourceExtent - 1);
        double fraction = Math.Clamp(clamped - i0, 0, 1);
        return new SupersampleTap(i0 * scale, i1 * scale, fraction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateTap(byte[] source, SupersampleTap rowTap, SupersampleTap colTap, bool opaque,
        ref double sumB, ref double sumG, ref double sumR, ref double sumA)
    {
        int i00 = rowTap.Offset0 + colTap.Offset0;
        int i10 = rowTap.Offset0 + colTap.Offset1;
        int i01 = rowTap.Offset1 + colTap.Offset0;
        int i11 = rowTap.Offset1 + colTap.Offset1;
        double fx = colTap.Fraction;
        double fy = rowTap.Fraction;
        sumB += Math.Round(Bilerp(source[i00], source[i10], source[i01], source[i11], fx, fy));
        sumG += Math.Round(Bilerp(source[i00 + 1], source[i10 + 1], source[i01 + 1], source[i11 + 1], fx, fy));
        sumR += Math.Round(Bilerp(source[i00 + 2], source[i10 + 2], source[i01 + 2], source[i11 + 2], fx, fy));
        if (!opaque)
        {
            sumA += Math.Round(Bilerp(source[i00 + 3], source[i10 + 3], source[i01 + 3], source[i11 + 3], fx, fy));
        }
    }

    // Vector form of the scalar row loop: the four BGRA channels ride in one Vector256<double>, using the same
    // IEEE add/sub/mul/div sequence and round-half-to-even as the scalar path (no FMA), so output stays identical.
    private static void ResampleRowAvx2(byte[] source, SupersampleTap[] columnTaps, SupersampleTap rowTap0, SupersampleTap rowTap1,
        byte[] destination, int destIndex, int destWidth, bool opaque)
    {
        ref byte sourceRef = ref MemoryMarshal.GetArrayDataReference(source);
        for (int col = 0; col < destWidth; col++, destIndex += 4)
        {
            var colTap0 = columnTaps[col * 2];
            var colTap1 = columnTaps[(col * 2) + 1];
            var sum = Vector256<double>.Zero;
            int samples = 0;

            if (rowTap0.Valid)
            {
                if (colTap0.Valid) { sum += SampleTapAvx2(ref sourceRef, rowTap0, colTap0); samples++; }
                if (colTap1.Valid) { sum += SampleTapAvx2(ref sourceRef, rowTap0, colTap1); samples++; }
            }

            if (rowTap1.Valid)
            {
                if (colTap0.Valid) { sum += SampleTapAvx2(ref sourceRef, rowTap1, colTap0); samples++; }
                if (colTap1.Valid) { sum += SampleTapAvx2(ref sourceRef, rowTap1, colTap1); samples++; }
            }

            uint packed;
            if (samples == 0)
            {
                packed = 0;
            }
            else
            {
                var average = Avx.RoundToNearestInteger(sum / Vector256.Create((double)samples));
                var ints = Avx.ConvertToVector128Int32WithTruncation(average);
                var shorts = Sse2.PackSignedSaturate(ints, ints);
                packed = Sse2.PackUnsignedSaturate(shorts, shorts).AsUInt32().ToScalar();
            }

            if (opaque)
            {
                packed |= 0xFF000000u;
            }

            Unsafe.WriteUnaligned(ref destination[destIndex], packed);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> SampleTapAvx2(ref byte sourceRef, SupersampleTap rowTap, SupersampleTap colTap)
    {
        var p00 = LoadBgraAsDouble(ref sourceRef, rowTap.Offset0 + colTap.Offset0);
        var p10 = LoadBgraAsDouble(ref sourceRef, rowTap.Offset0 + colTap.Offset1);
        var p01 = LoadBgraAsDouble(ref sourceRef, rowTap.Offset1 + colTap.Offset0);
        var p11 = LoadBgraAsDouble(ref sourceRef, rowTap.Offset1 + colTap.Offset1);
        var fx = Vector256.Create(colTap.Fraction);
        var fy = Vector256.Create(rowTap.Fraction);
        var top = p00 + ((p10 - p00) * fx);
        var bottom = p01 + ((p11 - p01) * fx);
        return Avx.RoundToNearestInteger(top + ((bottom - top) * fy));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> LoadBgraAsDouble(ref byte sourceRef, int offset)
    {
        // Offsets come from clamped taps and the caller verified source.Length >= width * height * 4.
        uint pixel = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref sourceRef, offset));
        return Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(pixel).AsByte()));
    }

    // Same expression order as SampleBgraBilinear's Lerp so results are bit-identical.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Bilerp(double p00, double p10, double p01, double p11, double fx, double fy)
    {
        double top = p00 + ((p10 - p00) * fx);
        double bottom = p01 + ((p11 - p01) * fx);
        return top + ((bottom - top) * fy);
    }

    private static int PositiveMod(int value, int modulo) => modulo <= 0 ? 0 : (value % modulo + modulo) % modulo;

    private static double PositiveMod(double value, int modulo)
    {
        if (modulo <= 0)
        {
            return 0;
        }

        double result = value % modulo;
        if (result < 0)
        {
            result += modulo;
        }

        return result;
    }

    private static double NormalizeScale(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }

        return value;
    }

    private static int ClampToInt(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
