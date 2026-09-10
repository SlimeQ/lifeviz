using System.Runtime.InteropServices;

namespace lifeviz;

internal static class RecordingFrameScaler
{
    // Expand each source row once, then copy it to the remaining destination
    // rows. Packed pixels preserve all four channels without per-byte stores.
    public static void Scale(byte[] source, byte[] destination, int width, int height, int scale)
    {
        int sourceLength = checked(width * height * 4);
        if (width <= 0 || height <= 0 || scale <= 0 ||
            source.Length < sourceLength || destination.Length < checked(sourceLength * scale * scale))
        {
            throw new ArgumentException("Invalid recording frame dimensions or buffers.");
        }

        if (scale == 1)
        {
            source.AsSpan(0, sourceLength).CopyTo(destination);
            return;
        }

        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(source.AsSpan(0, sourceLength));
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());
        int outputWidth = width * scale;
        for (int y = 0; y < height; y++)
        {
            Span<uint> row = output.Slice(y * scale * outputWidth, outputWidth);
            for (int x = 0; x < width; x++)
            {
                row.Slice(x * scale, scale).Fill(pixels[y * width + x]);
            }

            for (int repeat = 1; repeat < scale; repeat++)
            {
                row.CopyTo(output.Slice((y * scale + repeat) * outputWidth, outputWidth));
            }
        }
    }
}
