using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShotTrans.Interop;

namespace ShotTrans.Capture;

public static class ScreenCapture
{
    public static Bitmap Capture(Rectangle rect)
    {
        rect = Normalize(rect);
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("截图区域为空");

        var virtualScreen = NativeMethods.GetVirtualScreen();

        // 常见路径：GetDC(NULL) 覆盖整个虚拟桌面，坐标使用屏幕绝对坐标。
        var first = BitBltToBitmap(rect, rect.Left, rect.Top);
        if (!IsBlank(first)) return first;

        // 负坐标副屏等情况下，DC 原点可能位于虚拟桌面左上角，重试一次。
        if (virtualScreen.Left != 0 || virtualScreen.Top != 0)
        {
            var second = BitBltToBitmap(rect, rect.Left - virtualScreen.Left, rect.Top - virtualScreen.Top);
            if (!IsBlank(second))
            {
                first.Dispose();
                return second;
            }
            second.Dispose();
        }
        return first;
    }

    private static Bitmap BitBltToBitmap(Rectangle rect, int sourceX, int sourceY)
    {
        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdcDest = g.GetHdc();
        IntPtr hdcSrc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (hdcSrc == IntPtr.Zero) throw new Win32Exception("GetDC 失败");
            bool ok = NativeMethods.BitBlt(hdcDest, 0, 0, rect.Width, rect.Height, hdcSrc,
                sourceX, sourceY, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                bmp.Dispose();
                throw new Win32Exception(err, "BitBlt 截图失败");
            }
            return bmp;
        }
        finally
        {
            g.ReleaseHdc(hdcDest);
            if (hdcSrc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hdcSrc);
        }
    }

    public static Rectangle Normalize(Rectangle rect)
    {
        var vs = NativeMethods.GetVirtualScreen();
        int left = Math.Max(vs.Left, Math.Min(vs.Right, rect.Left));
        int top = Math.Max(vs.Top, Math.Min(vs.Bottom, rect.Top));
        int right = Math.Max(vs.Left, Math.Min(vs.Right, rect.Right));
        int bottom = Math.Max(vs.Top, Math.Min(vs.Bottom, rect.Bottom));
        return Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));
    }

    /// <summary>全黑通常意味着独占全屏 / 受保护内容无法通过 GDI 抓取。</summary>
    public static bool IsBlank(Bitmap bmp)
    {
        int stepX = Math.Max(1, bmp.Width / 64);
        int stepY = Math.Max(1, bmp.Height / 64);
        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > 3 || c.G > 3 || c.B > 3) return false;
            }
        }
        return true;
    }

    public static byte[]? MakeThumbnailPng(Bitmap source, int width = 320)
    {
        try
        {
            if (source.Width <= 0 || source.Height <= 0) return null;
            int w = Math.Min(width, source.Width);
            int h = Math.Max(1, (int)Math.Round(source.Height * (double)w / source.Width));
            using var thumb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            thumb.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("生成缩略图失败", ex);
            return null;
        }
    }
}
