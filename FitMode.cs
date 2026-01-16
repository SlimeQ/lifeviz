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

    private static int PositiveMod(int value, int modulo) => modulo <= 0 ? 0 : (value % modulo + modulo) % modulo;

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
