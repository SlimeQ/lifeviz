using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace lifeviz;

    internal sealed class WindowCaptureService
    {
        public WindowCaptureFrame? CaptureFrame(WindowHandleInfo info, int targetColumns, int targetRows, double threshold)
        {
            if (targetColumns <= 0 || targetRows <= 0)
            {
                return null;
            }

            if (!NativeMethods.IsWindow(info.Handle))
            {
                return null;
            }

            using var bitmap = CaptureWindowBitmap(info.Handle);
            if (bitmap == null)
            {
                return null;
            }

            return DownscaleToFrame(bitmap, targetColumns, targetRows, threshold);
        }

        // Fetches the physical window bounds, preferring the DWM extended frame bounds so DPI virtualization
        // doesn't truncate captures on per-monitor DPI setups.
        private static bool TryGetWindowBounds(IntPtr handle, out RECT rect)
        {
            int rectSize = Marshal.SizeOf<RECT>();
            if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, rectSize) == 0)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(handle, out rect))
            {
                rect = default;
                return false;
            }

            uint dpi = NativeMethods.GetDpiForWindow(handle);
            if (dpi > 0 && dpi != 96)
            {
                double scale = dpi / 96d;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                rect.Right = rect.Left + (int)Math.Round(width * scale);
                rect.Bottom = rect.Top + (int)Math.Round(height * scale);
            }

            return true;
        }

        private static bool TryGetClientBounds(IntPtr handle, out RECT clientRect, out int offsetX, out int offsetY)
        {
            clientRect = default;
            offsetX = 0;
            offsetY = 0;

            if (!NativeMethods.GetClientRect(handle, out var localClient))
            {
                return false;
            }

            var topLeft = new POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(handle, ref topLeft))
            {
                return false;
            }

            if (!TryGetWindowBounds(handle, out var windowRect))
            {
                return false;
            }

            clientRect.Left = topLeft.X;
            clientRect.Top = topLeft.Y;
            clientRect.Right = topLeft.X + localClient.Right;
            clientRect.Bottom = topLeft.Y + localClient.Bottom;

            offsetX = clientRect.Left - windowRect.Left;
            offsetY = clientRect.Top - windowRect.Top;
            return true;
        }

        public IReadOnlyList<WindowHandleInfo> EnumerateWindows(IntPtr excludeHandle)
        {
            var windows = new List<WindowHandleInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == excludeHandle)
            {
                return true;
            }

            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            if (NativeMethods.IsIconic(hWnd))
            {
                return true;
            }

            int length = NativeMethods.GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var builder = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hWnd, builder, builder.Capacity);
            string title = builder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!TryGetWindowBounds(hWnd, out var rect))
            {
                return true;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return true;
            }

            windows.Add(new WindowHandleInfo(hWnd, title, width, height));
            return true;
        }, IntPtr.Zero);

        windows.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return windows;
    }

    public bool[,]? CaptureMask(WindowHandleInfo info, int targetColumns, int targetRows, double threshold)
    {
        if (targetColumns <= 0 || targetRows <= 0)
        {
            return null;
        }

        if (!NativeMethods.IsWindow(info.Handle))
        {
            return null;
        }

        using var bitmap = CaptureWindowBitmap(info.Handle);
        if (bitmap == null)
        {
            return null;
        }

            return DownscaleToMask(bitmap, targetColumns, targetRows, threshold);
        }

    private static Bitmap? CaptureWindowBitmap(IntPtr handle)
    {
        if (!TryGetWindowBounds(handle, out var rect))
        {
            return null;
        }

        if (!TryGetClientBounds(handle, out var clientRect, out int offsetX, out int offsetY))
        {
            clientRect = rect;
            offsetX = 0;
            offsetY = 0;
        }

        int windowWidth = rect.Right - rect.Left;
        int windowHeight = rect.Bottom - rect.Top;
        int clientWidth = clientRect.Right - clientRect.Left;
        int clientHeight = clientRect.Bottom - clientRect.Top;
        if (windowWidth <= 0 || windowHeight <= 0 || clientWidth <= 0 || clientHeight <= 0)
        {
            return null;
        }

        IntPtr hdcWindow = NativeMethods.GetWindowDC(handle);
        if (hdcWindow == IntPtr.Zero)
        {
            return null;
        }

        IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcWindow, windowWidth, windowHeight);
        if (hBitmap == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(hdcMem);
            NativeMethods.ReleaseDC(handle, hdcWindow);
            return null;
        }

        IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

        bool captured = false;
        // Try PrintWindow to render layered/offscreen content (common for PiP).
        if (NativeMethods.PrintWindow(handle, hdcMem, NativeMethods.PW_RENDERFULLCONTENT))
        {
            captured = true;
        }
        else
        {
            // Fallback to BitBlt of the client area from the window DC.
            captured = NativeMethods.BitBlt(hdcMem, 0, 0, windowWidth, windowHeight, hdcWindow, 0, 0,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
        }

        NativeMethods.SelectObject(hdcMem, hOld);
        NativeMethods.DeleteDC(hdcMem);
        NativeMethods.ReleaseDC(handle, hdcWindow);

        if (!captured)
        {
            NativeMethods.DeleteObject(hBitmap);
            return null;
        }

        Bitmap? bmp = null;
        try
        {
            bmp = Image.FromHbitmap(hBitmap);
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }

        // Crop to client area to drop title bars/chrome.
        if (offsetX == 0 && offsetY == 0 && clientWidth == windowWidth && clientHeight == windowHeight)
        {
            return bmp;
        }

        try
        {
            var crop = new Rectangle(offsetX, offsetY, Math.Min(clientWidth, bmp.Width - offsetX), Math.Min(clientHeight, bmp.Height - offsetY));
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                return bmp;
            }

            var clientBmp = bmp.Clone(crop, PixelFormat.Format32bppArgb);
            bmp.Dispose();
            return clientBmp;
        }
        catch
        {
            return bmp;
        }
    }

    private static WindowCaptureFrame DownscaleToFrame(Bitmap bitmap, int columns, int rows, double threshold)
    {
        bool[,] mask = new bool[rows, columns];
        byte[] overlay = new byte[rows * columns * 4];
        double scaleX = bitmap.Width / (double)columns;
        double scaleY = bitmap.Height / (double)rows;
        threshold = Math.Clamp(threshold, 0, 1);

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        byte[] overlaySource;
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            bool bottomUp = stride < 0;
            int absoluteStride = Math.Abs(stride);
            int bufferLength = absoluteStride * bitmap.Height;
            var buffer = new byte[bufferLength];
            Marshal.Copy(data.Scan0, buffer, 0, bufferLength);
            overlaySource = new byte[bitmap.Width * bitmap.Height * 4];

            if (!bottomUp && absoluteStride == bitmap.Width * 4)
            {
                Buffer.BlockCopy(buffer, 0, overlaySource, 0, overlaySource.Length);
            }
            else
            {
                for (int srcRow = 0; srcRow < bitmap.Height; srcRow++)
                {
                    int bufferRow = bottomUp ? (bitmap.Height - 1 - srcRow) : srcRow;
                    int srcOffset = bufferRow * absoluteStride;
                    int destOffset = srcRow * bitmap.Width * 4;
                    Buffer.BlockCopy(buffer, srcOffset, overlaySource, destOffset, bitmap.Width * 4);
                }
            }

            for (int row = 0; row < rows; row++)
            {
                int srcY = Math.Min(bitmap.Height - 1, (int)Math.Floor(row * scaleY));
                int bufferRow = bottomUp ? (bitmap.Height - 1 - srcY) : srcY;
                int rowOffset = bufferRow * absoluteStride;
                int overlayRowOffset = row * columns * 4;

                for (int col = 0; col < columns; col++)
                {
                    int srcX = Math.Min(bitmap.Width - 1, (int)Math.Floor(col * scaleX));
                    int index = rowOffset + (srcX * 4);
                    if (index < 0 || index + 2 >= buffer.Length)
                    {
                        continue;
                    }

                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];
                    double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                    mask[row, col] = luminance >= threshold;

                    int overlayIndex = overlayRowOffset + (col * 4);
                    overlay[overlayIndex] = b;
                    overlay[overlayIndex + 1] = g;
                    overlay[overlayIndex + 2] = r;
                    overlay[overlayIndex + 3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new WindowCaptureFrame(mask, overlay, columns, rows, overlaySource, bitmap.Width, bitmap.Height);
    }

    private static bool[,] DownscaleToMask(Bitmap bitmap, int columns, int rows, double threshold)
    {
        bool[,] mask = new bool[rows, columns];
        double scaleX = bitmap.Width / (double)columns;
        double scaleY = bitmap.Height / (double)rows;
        threshold = Math.Clamp(threshold, 0, 1);

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            bool bottomUp = stride < 0;
            int absoluteStride = Math.Abs(stride);
            int bufferLength = absoluteStride * bitmap.Height;
            var buffer = new byte[bufferLength];
            Marshal.Copy(data.Scan0, buffer, 0, bufferLength);

            for (int row = 0; row < rows; row++)
            {
                int srcY = Math.Min(bitmap.Height - 1, (int)Math.Floor(row * scaleY));
                int bufferRow = bottomUp ? (bitmap.Height - 1 - srcY) : srcY;
                int rowOffset = bufferRow * absoluteStride;
                for (int col = 0; col < columns; col++)
                {
                    int srcX = Math.Min(bitmap.Width - 1, (int)Math.Floor(col * scaleX));
                    int index = rowOffset + (srcX * 4);
                    if (index < 0 || index + 2 >= buffer.Length)
                    {
                        continue;
                    }

                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];
                    double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                    mask[row, col] = luminance >= threshold;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return mask;
    }

    private static class NativeMethods
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int PW_RENDERFULLCONTENT = 0x00000002;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    internal sealed class WindowCaptureFrame
    {
        public WindowCaptureFrame(bool[,] mask, byte[] overlayDownscaled, int downscaledWidth, int downscaledHeight,
            byte[] overlaySource, int sourceWidth, int sourceHeight)
        {
            Mask = mask;
            OverlayDownscaled = overlayDownscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            OverlaySource = overlaySource;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        public bool[,] Mask { get; }
        public byte[] OverlayDownscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[] OverlaySource { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
    }
}
