using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace lifeviz;

internal sealed class WindowCaptureService : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, WindowCaptureSession> _sessions = new();

    public WindowCaptureFrame? CaptureFrame(WindowHandleInfo info, int targetColumns, int targetRows, FitMode fitMode, bool includeSource = false)
    {
        if (targetColumns <= 0 || targetRows <= 0)
        {
            return null;
        }

        if (!NativeMethods.IsWindow(info.Handle))
        {
            RemoveCache(info.Handle);
            return null;
        }

        var session = _sessions.GetOrAdd(info.Handle, h => new WindowCaptureSession(h));
        return session.GetFrame(targetColumns, targetRows, fitMode, includeSource);
    }
    
    public void RemoveCache(IntPtr handle)
    {
        if (_sessions.TryRemove(handle, out var session))
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
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

            private static bool TryGetWindowBounds(IntPtr handle, out RECT rect)
            {
                // DwmGetWindowAttribute gives the "visual" bounds (no shadow),
                // but PrintWindow captures the "real" bounds (with shadow).
                // mixing them causes offset errors and crashes.
                // We must use GetWindowRect to match PrintWindow's output.
                return NativeMethods.GetWindowRect(handle, out rect);
            }    
    // Helper for internal logic to access client bounds
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

    private sealed class WindowCaptureSession : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _captureTask;
        private readonly object _lock = new();
        private readonly FrameCache _cache = new();
        
        private byte[]? _latestBuffer;
        private int _latestWidth;
        private int _latestHeight;
        
        private byte[]? _downscaleBuffer;
        private byte[]? _sourceCopyBuffer;

        public WindowCaptureSession(IntPtr handle)
        {
            _handle = handle;
            _captureTask = Task.Run(CaptureLoop);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _captureTask.Wait(500);
            }
            catch
            {
                // Ignore shutdown errors
            }
            _cts.Dispose();
            _cache.Dispose();
        }

        public WindowCaptureFrame? GetFrame(int targetColumns, int targetRows, FitMode fitMode, bool includeSource)
        {
            byte[]? bufferToProcess;
            int width, height;

            lock (_lock)
            {
                if (_latestBuffer == null || _latestWidth <= 0 || _latestHeight <= 0)
                {
                    return null;
                }

                // Check if we need to resize or if we can just use the buffer
                // We need a stable copy or we need to hold the lock while downscaling.
                // Downscaling inside lock is safer for memory but blocks the capture thread from updating.
                // Capture thread updates every ~16-30ms. Downscale takes ~1-2ms. 
                // Blocking capture thread briefly is fine.
                
                width = _latestWidth;
                height = _latestHeight;
                bufferToProcess = _latestBuffer;

                return DownscaleToFrame(bufferToProcess, width, height, targetColumns, targetRows, fitMode, includeSource);
            }
        }

        private async Task CaptureLoop()
        {
            Logger.Info($"Starting CaptureLoop for handle {_handle}");
            int successCount = 0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!NativeMethods.IsWindow(_handle))
                    {
                        Logger.Warn($"Window {_handle} is no longer valid. Exiting CaptureLoop.");
                        break;
                    }

                    if (CaptureAndSwap())
                    {
                        if (successCount == 0)
                        {
                            Logger.Info($"First frame captured for {_handle}. Buffer: {_latestWidth}x{_latestHeight}.");
                            // We can try to get bounds here to log them
                             if (TryGetWindowBounds(_handle, out var rect) && TryGetClientBounds(_handle, out var clientRect, out int offsetX, out int offsetY))
                             {
                                 int w = rect.Right - rect.Left;
                                 int h = rect.Bottom - rect.Top;
                                 int cw = clientRect.Right - clientRect.Left;
                                 int ch = clientRect.Bottom - clientRect.Top;
                                 Logger.Info($"Debug DPI: Window={w}x{h}, Client={cw}x{ch}, Offset={offsetX},{offsetY}");
                             }
                        }
                        successCount++;
                    }
                    else
                    {
                         // Optional: log failure if needed, but avoid spam
                    }
                    
                    // Target high FPS capture
                    await Task.Delay(1, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in CaptureLoop for {_handle}: {ex}", ex);
                    await Task.Delay(100, _cts.Token); // Backoff on error
                }
            }
            Logger.Info($"Exiting CaptureLoop for {_handle}");
        }

        private bool CaptureAndSwap()
        {
            if (!TryGetWindowBounds(_handle, out var rect))
            {
                return false;
            }

            int windowWidth = rect.Right - rect.Left;
            int windowHeight = rect.Bottom - rect.Top;
            if (windowWidth <= 0 || windowHeight <= 0)
            {
                return false;
            }

            IntPtr hdcWindow = NativeMethods.GetWindowDC(_handle);
            if (hdcWindow == IntPtr.Zero) return false;

            try
            {
                if (_cache.Width != windowWidth || _cache.Height != windowHeight || _cache.HdcMem == IntPtr.Zero || _cache.HBitmap == IntPtr.Zero)
                {
                    _cache.Dispose();
                    _cache.HdcMem = NativeMethods.CreateCompatibleDC(hdcWindow);
                    _cache.HBitmap = NativeMethods.CreateCompatibleBitmap(hdcWindow, windowWidth, windowHeight);
                    _cache.Width = windowWidth;
                    _cache.Height = windowHeight;
                }

                if (_cache.HBitmap == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr hOld = NativeMethods.SelectObject(_cache.HdcMem, _cache.HBitmap);

                try
                {
                    bool captured = NativeMethods.PrintWindow(_handle, _cache.HdcMem, NativeMethods.PW_RENDERFULLCONTENT);
                    if (!captured)
                    {
                        captured = NativeMethods.BitBlt(_cache.HdcMem, 0, 0, windowWidth, windowHeight, hdcWindow, 0, 0, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                    }

                    if (!captured)
                    {
                        return false;
                    }
                
                    var bmi = new NativeMethods.BITMAPINFO
                    {
                        bmiHeader =
                        {
                            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                            biWidth = windowWidth,
                            biHeight = -windowHeight, 
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = 0 // BI_RGB
                        }
                    };

                    int bufferSize = windowWidth * windowHeight * 4;
                    var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                    try
                    {
                        if (NativeMethods.GetDIBits(_cache.HdcMem, _cache.HBitmap, 0, (uint)windowHeight, buffer, ref bmi, 0) == 0)
                        {
                            return false;
                        }

                        // Handle cropping logic
                        byte[] finalBuffer = buffer;
                        int finalWidth = windowWidth;
                        int finalHeight = windowHeight;

                        if (TryGetClientBounds(_handle, out var clientRect, out int offsetX, out int offsetY))
                        {
                            int clientWidth = clientRect.Right - clientRect.Left;
                            int clientHeight = clientRect.Bottom - clientRect.Top;
                            
                            // Debug logging for DPI issues (only once per second roughly, or just first frame? let's do first frame logic in CaptureLoop)
                            // But we don't have access to successCount here easily. 
                            // We can check if dimensions changed significantly or just relies on the Loop logger.
                            
                            // Let's just trust the values for now, but if we need to debug:
                            // Logger.Info($"Bounds: Window={windowWidth}x{windowHeight}, Client={clientWidth}x{clientHeight}, Offset={offsetX},{offsetY}");
                        
                            if (!(offsetX == 0 && offsetY == 0 && clientWidth == windowWidth && clientHeight == windowHeight))
                            {
                                int cropWidth = Math.Min(clientWidth, windowWidth - offsetX);
                                int cropHeight = Math.Min(clientHeight, windowHeight - offsetY);
                                
                                if (cropWidth > 0 && cropHeight > 0)
                                {
                                    var croppedBuffer = ArrayPool<byte>.Shared.Rent(cropWidth * cropHeight * 4);
                                    Parallel.For(0, cropHeight, y =>
                                    {
                                        int sourceY = y + offsetY;
                                        int sourceIndex = (sourceY * windowWidth + offsetX) * 4;
                                        int destIndex = y * cropWidth * 4;
                                        Buffer.BlockCopy(buffer, sourceIndex, croppedBuffer, destIndex, cropWidth * 4);
                                    });
                                    
                                    finalBuffer = croppedBuffer;
                                    finalWidth = cropWidth;
                                    finalHeight = cropHeight;
                                }
                            }
                        }

                        // Now swap into _latestBuffer
                        lock (_lock)
                        {
                            int requiredSize = finalWidth * finalHeight * 4;
                            if (_latestBuffer == null || _latestBuffer.Length != requiredSize)
                            {
                                _latestBuffer = new byte[requiredSize];
                            }
                            
                            Buffer.BlockCopy(finalBuffer, 0, _latestBuffer, 0, requiredSize);
                            _latestWidth = finalWidth;
                            _latestHeight = finalHeight;
                        }

                        if (finalBuffer != buffer)
                        {
                            ArrayPool<byte>.Shared.Return(finalBuffer);
                        }
                        
                        return true;
                    }
                    finally
                    {
                         ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                finally
                {
                    NativeMethods.SelectObject(_cache.HdcMem, hOld);
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(_handle, hdcWindow);
            }
        }

        private WindowCaptureFrame DownscaleToFrame(byte[] sourceBuffer, int sourceWidth, int sourceHeight, int columns, int rows, FitMode fitMode, bool includeSource)
        {
            int downscaledLength = rows * columns * 4;
            if (_downscaleBuffer == null || _downscaleBuffer.Length != downscaledLength)
            {
                _downscaleBuffer = new byte[downscaledLength];
            }
            byte[] overlay = _downscaleBuffer;

            byte[]? overlaySource = null;
            if (includeSource)
            {
                if (_sourceCopyBuffer == null || _sourceCopyBuffer.Length != sourceBuffer.Length)
                {
                    _sourceCopyBuffer = new byte[sourceBuffer.Length];
                }
                overlaySource = _sourceCopyBuffer;
                Buffer.BlockCopy(sourceBuffer, 0, overlaySource, 0, sourceBuffer.Length);
            }
            else
            {
                _sourceCopyBuffer = null;
            }
            
            var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, columns, rows);
            
            Parallel.For(0, rows, row =>
            {
                int sourceRowOffset = 0;
                int overlayRowOffset = row * columns * 4;

                for (int col = 0; col < columns; col++)
                {
                    byte b = 0;
                    byte g = 0;
                    byte r = 0;
                    if (ImageFit.TryMapPixel(mapping, col, row, out int srcX, out int srcY))
                    {
                        sourceRowOffset = srcY * sourceWidth * 4;
                        int index = sourceRowOffset + (srcX * 4);
                        b = sourceBuffer[index];
                        g = sourceBuffer[index + 1];
                        r = sourceBuffer[index + 2];
                    }
                    
                    int overlayIndex = overlayRowOffset + (col * 4);
                    overlay[overlayIndex] = b;
                    overlay[overlayIndex + 1] = g;
                    overlay[overlayIndex + 2] = r;
                    overlay[overlayIndex + 3] = 255;
                }
            });
            
            // We must return copies or handle buffer lifecycle carefully. 
            // WindowCaptureFrame is used by MainWindow.
            // The OverlayDownscaled buffer is just passed to MainWindow. 
            // MainWindow composites it immediately. 
            // However, next call to GetFrame reuses _downscaleBuffer.
            // If MainWindow is multithreaded or stores the frame, this is a race.
            // MainWindow seems to use it in InjectCaptureFrames -> BuildCompositeFrame -> CopyIntoBuffer
            // which happens sequentially in the render loop. So it should be fine to reuse the buffer 
            // IF we assume strictly sequential usage in MainWindow.
            
            // But to be safe (and standard), let's copy the result to a new buffer or return a new array?
            // WebcamCaptureService reuses the buffer:
            // return new WebcamFrame(data.DownscaledBuffer, ...)
            // So reusing is the established pattern here.
            
            return new WindowCaptureFrame(overlay, columns, rows,
                includeSource ? overlaySource : null,
                sourceWidth,
                sourceHeight);
        }
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

    private sealed class FrameCache : IDisposable
    {
        public IntPtr HdcMem = IntPtr.Zero;
        public IntPtr HBitmap = IntPtr.Zero;
        public int Width;
        public int Height;

        public void Dispose()
        {
            if (HBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(HBitmap);
                HBitmap = IntPtr.Zero;
            }
            if (HdcMem != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(HdcMem);
                HdcMem = IntPtr.Zero;
            }
        }
    }
}
