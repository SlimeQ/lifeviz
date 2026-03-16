using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace lifeviz;

public partial class MainWindow
{
    private sealed class CpuSourceCompositor
    {
        private readonly MainWindow _owner;

        public CpuSourceCompositor(MainWindow owner)
        {
            _owner = owner;
        }

        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime)
        {
            if (sources.Count == 0)
            {
                return null;
            }

            if (!_owner.TryGetDownscaledDimensions(sources, useEngineDimensions, out int downscaledWidth, out int downscaledHeight))
            {
                return null;
            }

            int downscaledLength = downscaledWidth * downscaledHeight * 4;
            if (downscaledBuffer == null || downscaledBuffer.Length != downscaledLength)
            {
                downscaledBuffer = new byte[downscaledLength];
            }

            bool wroteDownscaled = false;
            bool primedDownscaled = false;

            foreach (var source in sources)
            {
                var frame = source.LastFrame;
                if (frame == null)
                {
                    continue;
                }

                if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
                {
                    source.Window = source.Window.WithDimensions(frame.SourceWidth, frame.SourceHeight);
                }

                var downscaledTransform = _owner.BuildAnimationTransform(source, downscaledWidth, downscaledHeight, animationTime);
                double animationOpacity = _owner.BuildAnimationOpacity(source, animationTime);
                double effectiveOpacity = Math.Clamp(source.Opacity * animationOpacity, 0.0, 1.0);
                var keying = new KeyingSettings(
                    source.KeyEnabled && source.BlendMode == BlendMode.Normal,
                    source.BlendMode == BlendMode.Normal,
                    source.KeyColorR,
                    source.KeyColorG,
                    source.KeyColorB,
                    source.KeyTolerance);

                if (!primedDownscaled)
                {
                    CopyIntoBuffer(
                        downscaledBuffer,
                        downscaledWidth,
                        downscaledHeight,
                        frame.Downscaled,
                        frame.DownscaledWidth,
                        frame.DownscaledHeight,
                        effectiveOpacity,
                        source.Mirror && source.Type == CaptureSource.SourceType.Webcam,
                        source.FitMode,
                        downscaledTransform,
                        keying);
                    primedDownscaled = true;
                    wroteDownscaled = true;
                }
                else
                {
                    CompositeIntoBuffer(
                        downscaledBuffer,
                        downscaledWidth,
                        downscaledHeight,
                        frame.Downscaled,
                        frame.DownscaledWidth,
                        frame.DownscaledHeight,
                        source.BlendMode,
                        effectiveOpacity,
                        source.Mirror && source.Type == CaptureSource.SourceType.Webcam,
                        source.FitMode,
                        downscaledTransform,
                        keying);
                    wroteDownscaled = true;
                }
            }

            if (!wroteDownscaled)
            {
                return null;
            }

            return new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight);
        }

        internal void CompositeSourceFrameIntoBuffer(
            byte[] destination,
            int destWidth,
            int destHeight,
            SourceFrame frame,
            CaptureSource source,
            double animationTime,
            bool firstLayer)
        {
            if (destination.Length < destWidth * destHeight * 4)
            {
                return;
            }

            var transform = _owner.BuildAnimationTransform(source, destWidth, destHeight, animationTime);
            double animationOpacity = _owner.BuildAnimationOpacity(source, animationTime);
            double effectiveOpacity = Math.Clamp(source.Opacity * animationOpacity, 0.0, 1.0);
            var keying = new KeyingSettings(
                source.KeyEnabled && source.BlendMode == BlendMode.Normal,
                source.BlendMode == BlendMode.Normal,
                source.KeyColorR,
                source.KeyColorG,
                source.KeyColorB,
                source.KeyTolerance);

            if (firstLayer)
            {
                CopyIntoBuffer(
                    destination,
                    destWidth,
                    destHeight,
                    frame.Downscaled,
                    frame.DownscaledWidth,
                    frame.DownscaledHeight,
                    effectiveOpacity,
                    source.Mirror && source.Type == CaptureSource.SourceType.Webcam,
                    source.FitMode,
                    transform,
                    keying);
                return;
            }

            CompositeIntoBuffer(
                destination,
                destWidth,
                destHeight,
                frame.Downscaled,
                frame.DownscaledWidth,
                frame.DownscaledHeight,
                source.BlendMode,
                effectiveOpacity,
                source.Mirror && source.Type == CaptureSource.SourceType.Webcam,
                source.FitMode,
                transform,
                keying);
        }

        private static double ComputeKeyAlpha(byte sb, byte sg, byte sr, in KeyingSettings keying)
        {
            if (!keying.Enabled)
            {
                return 1.0;
            }

            int dr = sr - keying.R;
            int dg = sg - keying.G;
            int db = sb - keying.B;
            double distance = Math.Sqrt((dr * dr) + (dg * dg) + (db * db)) / MaxColorDistance;
            double tolerance = Math.Clamp(keying.Tolerance, 0.0, 1.0);
            if (tolerance <= 0.0)
            {
                return distance <= 0.0 ? 0.0 : 1.0;
            }

            return Math.Clamp(distance / tolerance, 0.0, 1.0);
        }

        private static void CopyIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
            double opacity, bool mirror, FitMode fitMode, Transform2D transform, in KeyingSettings keying)
        {
            opacity = Math.Clamp(opacity, 0.0, 1.0);
            int destStride = destWidth * 4;
            int sourceStride = sourceWidth * 4;
            var keyingLocal = keying;

            if (destWidth == sourceWidth && destHeight == sourceHeight && transform.IsIdentity)
            {
                Parallel.For(0, destHeight, row =>
                {
                    int destRowOffset = row * destStride;
                    int srcRowOffset = row * sourceStride;
                    for (int col = 0; col < destWidth; col++)
                    {
                        int destIndex = destRowOffset + (col * 4);
                        int sampleX = mirror ? (sourceWidth - 1 - col) : col;
                        int srcIndex = srcRowOffset + (sampleX * 4);
                        byte sb = source[srcIndex];
                        byte sg = source[srcIndex + 1];
                        byte sr = source[srcIndex + 2];
                        byte sa = source[srcIndex + 3];

                        double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                        double alpha = keyingLocal.UseAlpha ? (sa / 255.0) : 1.0;
                        double effectiveOpacity = opacity * keyAlpha * alpha;
                        destination[destIndex] = ClampToByte((int)(sb * effectiveOpacity));
                        destination[destIndex + 1] = ClampToByte((int)(sg * effectiveOpacity));
                        destination[destIndex + 2] = ClampToByte((int)(sr * effectiveOpacity));
                        destination[destIndex + 3] = 255;
                    }
                });
                return;
            }

            var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
            bool useTransform = !transform.IsIdentity;

            Parallel.For(0, destHeight, row =>
            {
                int destRowOffset = row * destStride;
                for (int col = 0; col < destWidth; col++)
                {
                    int destIndex = destRowOffset + (col * 4);
                    byte sb = 0;
                    byte sg = 0;
                    byte sr = 0;
                    byte sa = 255;
                    bool mapped;
                    if (useTransform)
                    {
                        transform.TransformPoint(col + 0.5, row + 0.5, out double tx, out double ty);
                        mapped = ImageFit.TrySampleMappedBgra(source, sourceWidth, sourceHeight, mapping,
                            tx, ty, mirror, out sb, out sg, out sr, out sa);
                    }
                    else
                    {
                        mapped = ImageFit.TrySampleMappedBgra(source, sourceWidth, sourceHeight, mapping,
                            col + 0.5, row + 0.5, mirror, out sb, out sg, out sr, out sa);
                    }

                    double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                    double alpha = keyingLocal.UseAlpha ? (sa / 255.0) : 1.0;
                    double effectiveOpacity = opacity * keyAlpha * alpha;
                    if (!mapped)
                    {
                        effectiveOpacity = 0.0;
                    }

                    destination[destIndex] = ClampToByte((int)(sb * effectiveOpacity));
                    destination[destIndex + 1] = ClampToByte((int)(sg * effectiveOpacity));
                    destination[destIndex + 2] = ClampToByte((int)(sr * effectiveOpacity));
                    destination[destIndex + 3] = 255;
                }
            });
        }

        private static void CompositeIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
            BlendMode mode, double opacity, bool mirror, FitMode fitMode, Transform2D transform, in KeyingSettings keying)
        {
            if (destWidth <= 0 || destHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            int destLength = destWidth * destHeight * 4;
            int sourceLength = sourceWidth * sourceHeight * 4;
            if (destination.Length < destLength || source.Length < sourceLength)
            {
                return;
            }

            int destStride = destWidth * 4;
            int sourceStride = sourceWidth * 4;

            var keyingLocal = keying;
            bool applyKeying = keyingLocal.Enabled && mode == BlendMode.Normal;
            if (destWidth == sourceWidth && destHeight == sourceHeight && transform.IsIdentity)
            {
                Parallel.For(0, destHeight, row =>
                {
                    int destRowOffset = row * destStride;
                    int srcRowOffset = row * sourceStride;
                    for (int col = 0; col < destWidth; col++)
                    {
                        int destIndex = destRowOffset + (col * 4);
                        int sampleX = mirror ? (sourceWidth - 1 - col) : col;
                        int srcIndex = srcRowOffset + (sampleX * 4);
                        byte sb = source[srcIndex];
                        byte sg = source[srcIndex + 1];
                        byte sr = source[srcIndex + 2];
                        byte sa = source[srcIndex + 3];
                        if (applyKeying)
                        {
                            double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                            sa = ClampToByte((int)Math.Round(sa * keyAlpha));
                        }
                        BlendInto(destination, destIndex, sb, sg, sr, sa, mode, opacity);
                    }
                });
                return;
            }

            var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
            bool useTransform = !transform.IsIdentity;
            Parallel.For(0, destHeight, row =>
            {
                int destRowOffset = row * destStride;
                for (int col = 0; col < destWidth; col++)
                {
                    int destIndex = destRowOffset + (col * 4);
                    byte sb = 0;
                    byte sg = 0;
                    byte sr = 0;
                    byte sa = 0;
                    bool mapped;
                    if (useTransform)
                    {
                        transform.TransformPoint(col + 0.5, row + 0.5, out double tx, out double ty);
                        mapped = ImageFit.TrySampleMappedBgra(source, sourceWidth, sourceHeight, mapping,
                            tx, ty, mirror, out sb, out sg, out sr, out sa);
                    }
                    else
                    {
                        mapped = ImageFit.TrySampleMappedBgra(source, sourceWidth, sourceHeight, mapping,
                            col + 0.5, row + 0.5, mirror, out sb, out sg, out sr, out sa);
                    }

                    if (!mapped)
                    {
                        continue;
                    }

                    if (applyKeying)
                    {
                        double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                        sa = ClampToByte((int)Math.Round(sa * keyAlpha));
                    }
                    BlendInto(destination, destIndex, sb, sg, sr, sa, mode, opacity);
                }
            });
        }

        private static void BlendInto(byte[] destination, int destIndex, byte sb, byte sg, byte sr, byte sa, BlendMode mode, double opacity)
        {
            opacity = Math.Clamp(opacity, 0.0, 1.0);
            byte db = destination[destIndex];
            byte dg = destination[destIndex + 1];
            byte dr = destination[destIndex + 2];

            int b;
            int g;
            int r;

            switch (mode)
            {
                case BlendMode.Additive:
                    b = db + sb;
                    g = dg + sg;
                    r = dr + sr;
                    break;
                case BlendMode.Normal:
                {
                    double alpha = (sa / 255.0) * opacity;
                    destination[destIndex] = ClampToByte((int)(db + (sb - db) * alpha));
                    destination[destIndex + 1] = ClampToByte((int)(dg + (sg - dg) * alpha));
                    destination[destIndex + 2] = ClampToByte((int)(dr + (sr - dr) * alpha));
                    destination[destIndex + 3] = 255;
                    return;
                }
                case BlendMode.Multiply:
                    b = db * sb / 255;
                    g = dg * sg / 255;
                    r = dr * sr / 255;
                    break;
                case BlendMode.Screen:
                    b = 255 - ((255 - db) * (255 - sb) / 255);
                    g = 255 - ((255 - dg) * (255 - sg) / 255);
                    r = 255 - ((255 - dr) * (255 - sr) / 255);
                    break;
                case BlendMode.Overlay:
                    b = db < 128 ? (2 * db * sb) / 255 : 255 - (2 * (255 - db) * (255 - sb) / 255);
                    g = dg < 128 ? (2 * dg * sg) / 255 : 255 - (2 * (255 - dg) * (255 - sg) / 255);
                    r = dr < 128 ? (2 * dr * sr) / 255 : 255 - (2 * (255 - dr) * (255 - sr) / 255);
                    break;
                case BlendMode.Lighten:
                    b = Math.Max(db, sb);
                    g = Math.Max(dg, sg);
                    r = Math.Max(dr, sr);
                    break;
                case BlendMode.Darken:
                    b = Math.Min(db, sb);
                    g = Math.Min(dg, sg);
                    r = Math.Min(dr, sr);
                    break;
                case BlendMode.Subtractive:
                    b = db - sb;
                    g = dg - sg;
                    r = dr - sr;
                    break;
                default:
                    b = sb;
                    g = sg;
                    r = sr;
                    break;
            }

            destination[destIndex] = ClampToByte((int)(db + (b - db) * opacity));
            destination[destIndex + 1] = ClampToByte((int)(dg + (g - dg) * opacity));
            destination[destIndex + 2] = ClampToByte((int)(dr + (r - dr) * opacity));
            destination[destIndex + 3] = 255;
        }
    }
}
