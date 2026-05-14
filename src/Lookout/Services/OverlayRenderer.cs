using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lookout.Services;

/// <summary>
/// Pure rendering for the highlight overlay — produces the per-frame ARGB
/// bitmap that OverlayService hands to UpdateLayeredWindow. Kept separate from
/// the Win32 windowing so the visual can be exercised on its own.
/// </summary>
public static class OverlayRenderer
{
    public const int OverlaySize = 220;
    public const int DurationMs = 4000;
    public const int PulsePeriodMs = 1300;

    private static readonly Color Accent = Color.FromArgb(255, 60, 130, 246);

    /// <summary>
    /// Renders one animation frame: concentric rings pulsing outward from a
    /// solid center dot, with a fade-in / fade-out envelope. Returns a
    /// premultiplied 32bpp ARGB bitmap ready for UpdateLayeredWindow.
    /// </summary>
    public static Bitmap RenderFrame(int elapsedMs)
    {
        var bitmap = new Bitmap(OverlaySize, OverlaySize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var center = OverlaySize / 2.0;
            var envelope = Envelope(elapsedMs);

            for (var k = 0; k < 3; k++)
            {
                var t = ((elapsedMs + k * PulsePeriodMs / 3.0) % PulsePeriodMs) / PulsePeriodMs;
                var radius = 22 + t * 80;
                var alpha = (int)(170 * (1 - t) * envelope);
                if (alpha <= 0)
                    continue;
                using var pen = new Pen(Color.FromArgb(alpha, Accent), 4f);
                g.DrawEllipse(pen,
                    (float)(center - radius), (float)(center - radius),
                    (float)(radius * 2), (float)(radius * 2));
            }

            var dotAlpha = (int)(230 * envelope);
            using (var brush = new SolidBrush(Color.FromArgb(dotAlpha, Accent)))
                g.FillEllipse(brush, (float)(center - 9), (float)(center - 9), 18, 18);
            using (var ring = new Pen(Color.FromArgb((int)(255 * envelope), Color.White), 2.5f))
                g.DrawEllipse(ring, (float)(center - 9), (float)(center - 9), 18, 18);
        }

        PremultiplyAlpha(bitmap);
        return bitmap;
    }

    /// <summary>Fade-in over the first 150ms, fade-out over the last 450ms.</summary>
    public static double Envelope(int elapsedMs)
    {
        if (elapsedMs < 0)
            return 0;
        if (elapsedMs < 150)
            return elapsedMs / 150.0;
        if (elapsedMs > DurationMs - 450)
            return Math.Max(0, (DurationMs - elapsedMs) / 450.0);
        return 1.0;
    }

    /// <summary>
    /// UpdateLayeredWindow expects premultiplied alpha; GDI+ gives us straight
    /// alpha, so convert in place.
    /// </summary>
    public static void PremultiplyAlpha(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var byteCount = data.Stride * data.Height;
            var buffer = new byte[byteCount];
            Marshal.Copy(data.Scan0, buffer, 0, byteCount);

            for (var i = 0; i < byteCount; i += 4)
            {
                // Pixel layout in memory is BGRA.
                var a = buffer[i + 3];
                buffer[i] = (byte)(buffer[i] * a / 255);
                buffer[i + 1] = (byte)(buffer[i + 1] * a / 255);
                buffer[i + 2] = (byte)(buffer[i + 2] * a / 255);
            }

            Marshal.Copy(buffer, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
