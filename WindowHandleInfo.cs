using System;

namespace lifeviz;

internal sealed class WindowHandleInfo
{
    public WindowHandleInfo(IntPtr handle, string title, int width, int height)
    {
        Handle = handle;
        Title = title;
        Width = width;
        Height = height;
    }

    public IntPtr Handle { get; }
    public string Title { get; }
    public int Width { get; }
    public int Height { get; }

    public double AspectRatio => Height <= 0 ? 16d / 9d : Math.Max(0.05, (double)Width / Height);

    public WindowHandleInfo WithDimensions(int width, int height) =>
        new(Handle, Title, width, height);
}
