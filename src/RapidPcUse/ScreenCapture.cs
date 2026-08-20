using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RapidPcUse;

internal interface IScreenCaptureBackend
{
    Observation Capture(long frameId, bool controlActive);

    Observation CaptureActiveWindow(long frameId, bool controlActive) => Capture(frameId, controlActive);
}

internal sealed class GdiScreenCaptureBackend : IScreenCaptureBackend
{
    private static int _imagingPipelineWarmed;
    private readonly CaptureTier _captureTier;
    private readonly int _jpegQuality;

    internal GdiScreenCaptureBackend()
    {
        _captureTier = CaptureResolutionPolicy.ReadEnvironmentTier();
        _jpegQuality = ReadBoundedEnvironmentInteger("RAPID_PC_JPEG_QUALITY", 84, 35, 100);
        TryWarmImagingPipeline(_jpegQuality);
    }

    public Observation Capture(long frameId, bool controlActive)
    {
        var monitors = MonitorManager.GetMonitors();
        var topologyKey = MonitorManager.GetTopologyKey(monitors);
        return CaptureRegions(frameId, controlActive, monitors, topologyKey, "full_desktop");
    }

    public Observation CaptureActiveWindow(long frameId, bool controlActive)
    {
        var monitors = MonitorManager.GetMonitors();
        var topologyKey = MonitorManager.GetTopologyKey(monitors);
        var region = GetActiveWindowRegion(monitors);
        return region is null
            ? CaptureRegions(frameId, controlActive, monitors, topologyKey, "full_desktop_fallback")
            : CaptureRegions(frameId, controlActive, [region], topologyKey, "active_window");
    }

    private Observation CaptureRegions(
        long frameId,
        bool controlActive,
        IReadOnlyList<MonitorDescriptor> regions,
        string topologyKey,
        string captureScope)
    {
        var stopwatch = Stopwatch.StartNew();
        ValidateCaptureResources(regions);
        ScreenFrame[] frames;
        if (regions.Count == 1)
        {
            frames = [CaptureMonitor(frameId, regions[0])];
        }
        else
        {
            var tasks = regions.Select(monitor => Task.Run(() => CaptureMonitor(frameId, monitor))).ToArray();
            Task.WaitAll(tasks);
            frames = tasks.Select(task => task.Result).ToArray();
        }

        return new Observation(frameId, topologyKey, frames, stopwatch.ElapsedMilliseconds, controlActive, captureScope);
    }

    internal static MonitorDescriptor? CreateActiveWindowRegion(
        NativeMethods.Rect window,
        IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var virtualLeft = monitors.Min(monitor => monitor.Left);
        var virtualTop = monitors.Min(monitor => monitor.Top);
        var virtualRight = monitors.Max(monitor => checked(monitor.Left + monitor.Width));
        var virtualBottom = monitors.Max(monitor => checked(monitor.Top + monitor.Height));
        var left = Math.Max(window.Left, virtualLeft);
        var top = Math.Max(window.Top, virtualTop);
        var right = Math.Min(window.Right, virtualRight);
        var bottom = Math.Min(window.Bottom, virtualBottom);
        var width = right - left;
        var height = bottom - top;
        if (width < 64 || height < 64)
        {
            return null;
        }

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        var intersectsPrimary = primary is not null &&
            left < primary.Left + primary.Width &&
            right > primary.Left &&
            top < primary.Top + primary.Height &&
            bottom > primary.Top;
        return new MonitorDescriptor(
            "window-foreground",
            "foreground-window",
            left,
            top,
            width,
            height,
            intersectsPrimary);
    }

    private static MonitorDescriptor? GetActiveWindowRegion(IReadOnlyList<MonitorDescriptor> monitors)
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == nint.Zero || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window) ||
            !NativeMethods.GetWindowRect(window, out var rectangle))
        {
            return null;
        }

        return CreateActiveWindowRegion(rectangle, monitors);
    }

    internal static void ValidateCaptureResources(IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0 || monitors.Count > SecurityLimits.MaxDisplays)
        {
            throw new InvalidOperationException($"Screen capture supports between 1 and {SecurityLimits.MaxDisplays} displays.");
        }

        long totalPixels = 0;
        foreach (var monitor in monitors)
        {
            if (monitor.Width <= 0 || monitor.Height <= 0)
            {
                throw new InvalidOperationException("A display reported invalid capture dimensions.");
            }

            var pixels = checked((long)monitor.Width * monitor.Height);
            if (pixels > SecurityLimits.MaxPixelsPerDisplay)
            {
                throw new InvalidOperationException("A display exceeds the per-display screenshot resource limit.");
            }

            totalPixels = checked(totalPixels + pixels);
            if (totalPixels > SecurityLimits.MaxTotalCapturePixels)
            {
                throw new InvalidOperationException("The desktop exceeds the total screenshot resource limit.");
            }
        }
    }

    private ScreenFrame CaptureMonitor(long frameId, MonitorDescriptor monitor)
    {
        var stopwatch = Stopwatch.StartNew();
        var stageTimestamp = Stopwatch.GetTimestamp();
        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed.");
        }

        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldObject = 0;
        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, monitor.Width, monitor.Height);
            if (memoryDc == 0 || bitmap == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to allocate the screenshot surface.");
            }

            var surfaceSetupMicroseconds = ElapsedMicroseconds(stageTimestamp);

            stageTimestamp = Stopwatch.GetTimestamp();
            oldObject = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                monitor.Width,
                monitor.Height,
                screenDc,
                monitor.Left,
                monitor.Top,
                NativeMethods.Srccopy | NativeMethods.CaptureBlt))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt screenshot capture failed.");
            }

            var blitMicroseconds = ElapsedMicroseconds(stageTimestamp);

            stageTimestamp = Stopwatch.GetTimestamp();
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var materializeMicroseconds = ElapsedMicroseconds(stageTimestamp);

            stageTimestamp = Stopwatch.GetTimestamp();
            var resolution = CaptureResolutionPolicy.Select(source.PixelWidth, source.PixelHeight, _captureTier);
            var encodedSource = Resize(source, resolution);
            var contentFingerprint = CreateContentFingerprint(encodedSource);
            var resizeMicroseconds = ElapsedMicroseconds(stageTimestamp);

            stageTimestamp = Stopwatch.GetTimestamp();
            var encoder = new JpegBitmapEncoder { QualityLevel = _jpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(encodedSource));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var encodeMicroseconds = ElapsedMicroseconds(stageTimestamp);
            stopwatch.Stop();

            return new ScreenFrame(
                frameId,
                monitor,
                encodedSource.PixelWidth,
                encodedSource.PixelHeight,
                "image/jpeg",
                stream.ToArray(),
                stopwatch.ElapsedMilliseconds,
                resolution,
                new CaptureStageTimings(
                    surfaceSetupMicroseconds,
                    blitMicroseconds,
                    0,
                    materializeMicroseconds,
                    resizeMicroseconds,
                    encodeMicroseconds,
                    TicksToMicroseconds(stopwatch.ElapsedTicks)),
                contentFingerprint);
        }
        finally
        {
            if (oldObject != 0 && memoryDc != 0)
            {
                _ = NativeMethods.SelectObject(memoryDc, oldObject);
            }

            if (bitmap != 0)
            {
                _ = NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = NativeMethods.DeleteDC(memoryDc);
            }

            _ = NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private static void TryWarmImagingPipeline(int jpegQuality)
    {
        if (Interlocked.Exchange(ref _imagingPipelineWarmed, 1) != 0)
        {
            return;
        }

        var screenDc = NativeMethods.GetDC(0);
        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldObject = 0;
        try
        {
            if (screenDc == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture warmup GetDC failed.");
            }

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, 1, 1);
            if (memoryDc == 0 || bitmap == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture warmup surface allocation failed.");
            }

            oldObject = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.PatBlt(memoryDc, 0, 0, 1, 1, NativeMethods.Blackness))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Capture warmup surface clear failed.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _ = CreateContentFingerprint(source);
            var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
        }
        catch (Exception exception) when (exception is Win32Exception or ExternalException or InvalidOperationException)
        {
            Interlocked.Exchange(ref _imagingPipelineWarmed, 0);
            DriverLog.Warning(
                "capture.warmup_failed",
                "The in-memory capture pipeline warmup failed; the first real capture will initialize it instead.",
                exception: exception);
        }
        finally
        {
            if (oldObject != 0 && memoryDc != 0)
            {
                _ = NativeMethods.SelectObject(memoryDc, oldObject);
            }

            if (bitmap != 0)
            {
                _ = NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                _ = NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != 0)
            {
                _ = NativeMethods.ReleaseDC(0, screenDc);
            }
        }
    }

    private static BitmapSource Resize(BitmapSource source, CaptureResolution resolution)
    {
        if (!resolution.Resized)
        {
            return source;
        }

        var scaleX = (double)resolution.Width / source.PixelWidth;
        var scaleY = (double)resolution.Height / source.PixelHeight;
        var resized = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        resized.Freeze();
        return resized;
    }

    // A tiny cursor-free grayscale image is enough to distinguish meaningful
    // UI changes without retaining readable screen content in agent memory.
    private static byte[] CreateContentFingerprint(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        var scaled = new TransformedBitmap(
            gray,
            new ScaleTransform(32d / gray.PixelWidth, 18d / gray.PixelHeight));
        scaled.Freeze();
        var bytes = new byte[32 * 18];
        scaled.CopyPixels(bytes, 32, 0);
        return bytes;
    }

    private static long ElapsedMicroseconds(long startTimestamp)
        => (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds * 1000);

    private static long TicksToMicroseconds(long elapsedTicks)
        => (long)(elapsedTicks * 1_000_000d / Stopwatch.Frequency);

    private static void DrawCursor(nint destinationDc, MonitorDescriptor monitor)
    {
        var cursor = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref cursor) || (cursor.Flags & NativeMethods.CursorShowing) == 0)
        {
            return;
        }

        if (cursor.ScreenPosition.X < monitor.Left || cursor.ScreenPosition.X >= monitor.Left + monitor.Width ||
            cursor.ScreenPosition.Y < monitor.Top || cursor.ScreenPosition.Y >= monitor.Top + monitor.Height)
        {
            return;
        }

        if (!NativeMethods.GetIconInfo(cursor.Cursor, out var iconInfo))
        {
            return;
        }

        try
        {
            var x = cursor.ScreenPosition.X - monitor.Left - (int)iconInfo.HotspotX;
            var y = cursor.ScreenPosition.Y - monitor.Top - (int)iconInfo.HotspotY;
            _ = NativeMethods.DrawIconEx(destinationDc, x, y, cursor.Cursor, 0, 0, 0, 0, NativeMethods.DiNormal);
        }
        finally
        {
            if (iconInfo.ColorBitmap != 0)
            {
                _ = NativeMethods.DeleteObject(iconInfo.ColorBitmap);
            }

            if (iconInfo.MaskBitmap != 0)
            {
                _ = NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            }
        }
    }

    private static int ReadBoundedEnvironmentInteger(string name, int fallback, int minimum, int maximum)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }
}
