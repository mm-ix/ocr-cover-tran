using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShotTrans.Interop;

namespace ShotTrans.Windows;

/// <summary>使用 UpdateLayeredWindow 的逐像素透明窗口；支持点击穿透。</summary>
internal abstract class LayeredWindow : NativeWindow, IDisposable
{
    /// <summary>生产环境默认排除自身，避免覆盖层被再次截图/识别；测试截图时可临时关闭。</summary>
    public static bool ExcludeFromCapture = true;

    private readonly bool _clickThrough;
    private readonly string _caption;
    private bool _shown;
    private bool _disposed;
    private bool _captureExcluded;

    public bool IsDisposed => _disposed;

    protected LayeredWindow(bool clickThrough, string caption = "ShotTrans.Overlay")
    {
        _clickThrough = clickThrough;
        _caption = caption;
    }

    private CreateParams BuildCreateParams()
    {
        var cp = new CreateParams
        {
            Style = NativeMethods.WS_POPUP,
            ExStyle = NativeMethods.WS_EX_LAYERED
                      | NativeMethods.WS_EX_TOOLWINDOW
                      | NativeMethods.WS_EX_TOPMOST
                      | NativeMethods.WS_EX_NOACTIVATE
                      | (_clickThrough ? NativeMethods.WS_EX_TRANSPARENT : 0),
            Caption = _caption,
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1
        };
        return cp;
    }

    protected void EnsureHandle()
    {
        if (Handle == IntPtr.Zero) CreateHandle(BuildCreateParams());
    }

    protected void ShowBitmap(Bitmap bitmap, Rectangle bounds)
    {
        if (_disposed) return;
        EnsureHandle();

        if (!_captureExcluded)
        {
            _captureExcluded = true;
            if (ExcludeFromCapture)
            {
                try { NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE); }
                catch { }
            }
        }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = bitmap.Width,
                    biHeight = -bitmap.Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                    biSizeImage = (uint)(bitmap.Width * bitmap.Height * 4)
                }
            };

            dib = NativeMethods.CreateDIBSection(memDc, ref bi, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");

            oldObj = NativeMethods.SelectObject(memDc, dib);

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = bitmap.Width * 4;
                if (data.Stride == rowBytes)
                {
                    long total = (long)rowBytes * bitmap.Height;
                    unsafeCopy(data.Scan0, bits, total);
                }
                else
                {
                    var row = new byte[Math.Abs(data.Stride)];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                        Marshal.Copy(row, 0, bits + y * rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var ptDst = new NativeMethods.POINT(bounds.X, bounds.Y);
            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var ptSrc = new NativeMethods.POINT(0, 0);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            if (!NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, (int)NativeMethods.ULW_ALPHA))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Warn("UpdateLayeredWindow 失败：" + err);
            }

            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y, bitmap.Width, bitmap.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            if (!_shown)
            {
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                _shown = true;
            }
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void unsafeCopy(IntPtr src, IntPtr dst, long bytes)
    {
        // 使用 Marshal.Copy 的分块版本，避免 unsafe 编译选项。
        const int chunk = 1024 * 1024;
        var buffer = new byte[chunk];
        long offset = 0;
        while (offset < bytes)
        {
            int n = (int)Math.Min(chunk, bytes - offset);
            Marshal.Copy(src + (int)offset, buffer, 0, n);
            Marshal.Copy(buffer, 0, dst + (int)offset, n);
            offset += n;
        }
    }

    protected void HideWindow()
    {
        if (Handle == IntPtr.Zero) return;
        try { NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE); } catch { }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HideWindow();
        try { DestroyHandle(); } catch { }
        GC.SuppressFinalize(this);
    }
}
