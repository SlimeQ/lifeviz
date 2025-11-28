using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace lifeviz;

    internal sealed class WindowCaptureService
    {
        private readonly Dictionary<IntPtr, FrameCache> _frameCache = new();

        public WindowCaptureFrame? CaptureFrame(WindowHandleInfo info, int targetColumns, int targetRows, double threshold, bool includeSource = true)
        {
            if (targetColumns <= 0 || targetRows <= 0)
            {
                return null;
            }

            if (!NativeMethods.IsWindow(info.Handle))
            {
                return null;
            }

            var capture = CaptureWindowBytes(info.Handle);
            if (capture == null)
            {
                return null;
            }

            var (buffer, width, height) = capture.Value;
            
            var cache = GetOrCreateCache(info.Handle);
            return DownscaleToFrame(buffer, width, height, targetColumns, targetRows, threshold, includeSource, cache);
        }

        private static bool TryGetWindowBounds(IntPtr handle, out RECT rect)
        {
            int rectSize = Marshal.SizeOf<RECT>();
            if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, rectSize) == 0)
            {
                return true;
            }
            
            return NativeMethods.GetWindowRect(handle, out rect);
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
    
        private (byte[] buffer, int width, int height)? CaptureWindowBytes(IntPtr handle)
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
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return null;
            }

            IntPtr hdcWindow = NativeMethods.GetWindowDC(handle);
            if (hdcWindow == IntPtr.Zero) return null;
            IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcWindow);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hdcWindow, windowWidth, windowHeight);

            if (hBitmap == IntPtr.Zero)
            {
                NativeMethods.DeleteDC(hdcMem);
                NativeMethods.ReleaseDC(handle, hdcWindow);
                return null;
            }

            IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);

            try
            {
                bool captured = NativeMethods.PrintWindow(handle, hdcMem, NativeMethods.PW_RENDERFULLCONTENT);
                if (!captured)
                {
                    captured = NativeMethods.BitBlt(hdcMem, 0, 0, windowWidth, windowHeight, hdcWindow, 0, 0, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                }

                if (!captured)
                {
                    return null;
                }
                
                var bmi = new NativeMethods.BITMAPINFO
                {
                    bmiHeader =
                    {
                        biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                        biWidth = windowWidth,
                        biHeight = -windowHeight, // Negative for top-down bitmap
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0 // BI_RGB
                    }
                };

                int bufferSize = windowWidth * windowHeight * 4;
                var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                if (NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)windowHeight, buffer, ref bmi, 0) == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return null;
                }
                
                int clientWidth = clientRect.Right - clientRect.Left;
                int clientHeight = clientRect.Bottom - clientRect.Top;
                
                if (offsetX == 0 && offsetY == 0 && clientWidth == windowWidth && clientHeight == windowHeight)
                {
                    // No cropping needed, return the full buffer
                    return (buffer, windowWidth, windowHeight);
                }

                int cropWidth = Math.Min(clientWidth, windowWidth - offsetX);
                int cropHeight = Math.Min(clientHeight, windowHeight - offsetY);
                if (cropWidth <= 0 || cropHeight <= 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return null;
                }

                var croppedBuffer = new byte[cropWidth * cropHeight * 4];
                Parallel.For(0, cropHeight, y =>
                {
                    int sourceY = y + offsetY;
                    int sourceIndex = (sourceY * windowWidth + offsetX) * 4;
                    int destIndex = y * cropWidth * 4;
                    Buffer.BlockCopy(buffer, sourceIndex, croppedBuffer, destIndex, cropWidth * 4);
                });
                
                ArrayPool<byte>.Shared.Return(buffer);
                return (croppedBuffer, cropWidth, cropHeight);
            }
            finally
            {
                NativeMethods.SelectObject(hdcMem, hOld);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(hdcMem);
                NativeMethods.ReleaseDC(handle, hdcWindow);
            }
        }

    private WindowCaptureFrame DownscaleToFrame(byte[] sourceBuffer, int sourceWidth, int sourceHeight, int columns, int rows, double threshold, bool includeSource, FrameCache cache)
    {
        int downscaledLength = rows * columns * 4;
        byte[] overlay = cache.OverlayDownscaled?.Length == downscaledLength
            ? cache.OverlayDownscaled!
            : cache.OverlayDownscaled = new byte[downscaledLength];

        byte[]? overlaySource = null;
        if (includeSource)
        {
            if (cache.OverlaySource == null || cache.OverlaySource.Length != sourceBuffer.Length)
            {
                cache.OverlaySource = new byte[sourceBuffer.Length];
            }
            overlaySource = cache.OverlaySource;
            Buffer.BlockCopy(sourceBuffer, 0, overlaySource, 0, sourceBuffer.Length);
        }
        else
        {
            cache.OverlaySource = null;
        }
        
        double scaleX = sourceWidth / (double)columns;
        double scaleY = sourceHeight / (double)rows;
        
        Parallel.For(0, rows, row =>
        {
            int srcY = Math.Min(sourceHeight - 1, (int)Math.Floor(row * scaleY));
            int sourceRowOffset = srcY * sourceWidth * 4;
            int overlayRowOffset = row * columns * 4;

            for (int col = 0; col < columns; col++)
            {
                int srcX = Math.Min(sourceWidth - 1, (int)Math.Floor(col * scaleX));
                int index = sourceRowOffset + (srcX * 4);

                byte b = sourceBuffer[index];
                byte g = sourceBuffer[index + 1];
                byte r = sourceBuffer[index + 2];
                
                int overlayIndex = overlayRowOffset + (col * 4);
                overlay[overlayIndex] = b;
                overlay[overlayIndex + 1] = g;
                overlay[overlayIndex + 2] = r;
                overlay[overlayIndex + 3] = 255;
            }
        });
        
        // Note: Mask is not calculated here anymore, it's done in the main window
        return new WindowCaptureFrame(overlay, columns, rows,
            includeSource ? overlaySource : null,
            sourceWidth,
            sourceHeight);
    }
    
    private static class NativeMethods
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int PW_RENDERFULLCONTENT = 0x00000002;
        public const uint DIB_RGB_COLORS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public RGBQUAD bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }

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
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);
        
        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
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
        public WindowCaptureFrame(byte[] overlayDownscaled, int downscaledWidth, int downscaledHeight,
            byte[]? overlaySource, int sourceWidth, int sourceHeight)
        {
            OverlayDownscaled = overlayDownscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            OverlaySource = overlaySource;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        public byte[] OverlayDownscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[]? OverlaySource { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
    }

    private sealed class FrameCache
    {
        public byte[]? OverlayDownscaled;
        public byte[]? OverlaySource;
    }

    private FrameCache GetOrCreateCache(IntPtr handle)
    {
        if (!_frameCache.TryGetValue(handle, out var cache))
        {
            cache = new FrameCache();
            _frameCache[handle] = cache;
        }

        return cache;
    }
}
