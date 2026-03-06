using System;

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
