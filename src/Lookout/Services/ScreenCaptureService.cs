using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Lookout.Services;

/// <summary>A captured display: its JPEG plus where it sits in screen coordinates.</summary>
public sealed record CapturedDisplay(byte[] Jpeg, int OriginX, int OriginY, int Width, int Height);

/// <summary>
/// Captures every connected display to a JPEG via GDI.
/// (Windows.Graphics.Capture is a future upgrade for protected-content support;
/// GDI is used here for broad compatibility and because it has no async/D3D
/// surface plumbing to get wrong.)
/// </summary>
public sealed class ScreenCaptureService
{
    private const int JpegQuality = 70;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>Captures each display. Returns one JPEG byte array per display.</summary>
    public IReadOnlyList<byte[]> CaptureAllDisplays()
    {
        var displays = CaptureDisplays();
        var jpegs = new List<byte[]>(displays.Count);
        foreach (var display in displays)
            jpegs.Add(display.Jpeg);
        return jpegs;
    }

    /// <summary>
    /// Captures each display along with its screen-coordinate origin — used by
    /// the highlight feature to map OCR pixel coordinates back to the screen.
    /// </summary>
    public IReadOnlyList<CapturedDisplay> CaptureDisplays()
    {
        var results = new List<CapturedDisplay>();
        foreach (var bounds in EnumerateMonitorBounds())
        {
            try
            {
                var jpeg = CaptureRegion(bounds);
                if (jpeg.Length > 0)
                    results.Add(new CapturedDisplay(
                        jpeg, bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            }
            catch
            {
                // Skip a display we couldn't grab rather than failing the whole capture.
            }
        }
        return results;
    }

    private static List<Rectangle> EnumerateMonitorBounds()
    {
        var monitors = new List<Rectangle>();

        bool Callback(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data)
        {
            monitors.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // Fallback: the whole virtual desktop as a single region.
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0)
                monitors.Add(new Rectangle(x, y, w, h));
        }

        return monitors;
    }

    private static byte[] CaptureRegion(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return Array.Empty<byte>();

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        return EncodeJpeg(bitmap);
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        var encoder = GetJpegEncoder();
        if (encoder != null)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);
            bitmap.Save(stream, encoder, parameters);
        }
        else
        {
            bitmap.Save(stream, ImageFormat.Jpeg);
        }
        return stream.ToArray();
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
                return codec;
        }
        return null;
    }
}
