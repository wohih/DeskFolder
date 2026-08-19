using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskFolder.Services;

/// <summary>主题相关的颜色工具：解析、自动对比色、HSV 与 RGB 互转（供颜色选择器使用）。</summary>
internal static class ThemeHelper
{
    /// <summary>方框类型显示名（索引对应 ThemeConfig.BorderStyle）。</summary>
    public static readonly string[] BorderStyleNames =
        { "实线", "虚线", "点线", "双线", "Windows 11 风格" };

    /// <summary>解析十六进制颜色（#RGB / #RRGGBB / #AARRGGBB），失败返回 false。</summary>
    public static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex.Trim());
            return true;
        }
        catch
        {
            color = Colors.Transparent;
            return false;
        }
    }

    /// <summary>将值夹取到 [min, max] 区间。</summary>
    public static double Clamp(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);

    /// <summary>根据背景亮度自动选择可读的文字颜色（深色底用白字，浅色底用黑字）。</summary>
    public static Color ContrastColor(Color c)
    {
        double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B; // 0-255
        return lum > 140 ? Colors.Black : Colors.White;
    }

    /// <summary>HSV → RGB。h∈[0,360)，s,v∈[0,1]。</summary>
    public static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>RGB → HSV。返回 h∈[0,360)，s,v∈[0,1]。</summary>
    public static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    /// <summary>颜色 → #RRGGBB 字符串。</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>常见视频扩展名（Windows Media Foundation 可解码，具体取决于系统已安装的解码器；
    /// mp4/wmv/avi/mov/m4v 兼容性最好，webm/mkv/flv 依赖系统编解码器）。</summary>
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".wmv", ".avi", ".mov", ".m4v", ".mpg", ".mpeg",
        ".mkv", ".webm", ".ts", ".m2ts", ".flv", ".3gp"
    };

    /// <summary>判断路径是否为常见视频文件（用于图片主题支持视频背景）。</summary>
    public static bool IsVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return VideoExts.Contains(Path.GetExtension(path));
    }

    private static BitmapSource? _videoThumb;
    /// <summary>视频文件的占位缩略图（深色方块 + 白色播放三角），用于主题编辑器的图片组列表。</summary>
    public static BitmapSource VideoThumbPlaceholder()
    {
        if (_videoThumb != null) return _videoThumb;
        const int s = 36;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(0, 0, s, s));
            var tri = new PathFigure { StartPoint = new Point(14, 9), IsClosed = true };
            tri.Segments.Add(new LineSegment(new Point(14, 27), true));
            tri.Segments.Add(new LineSegment(new Point(28, 18), true));
            var geo = new PathGeometry();
            geo.Figures.Add(tri);
            dc.DrawGeometry(new SolidColorBrush(Colors.White), null, geo);
        }
        var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var bmp = new WriteableBitmap(rtb);
        bmp.Freeze();
        _videoThumb = bmp;
        return _videoThumb;
    }

    /// <summary>用 Windows Shell 缩略图接口（IShellItemImageFactory）提取视频海报帧作为裁剪预览。
    /// 这比「离屏 MediaElement + RenderTargetBitmap」可靠——后者对硬件解码视频永远抓到黑屏
    /// （EVR 合成层不在 WPF 保留模式树内，Render 不到）。失败返回 null（调用方回退占位图）。</summary>
    public static BitmapSource? GrabVideoThumbnail(string path)
    {
        Diag.Reset();
        long fileSize = 0;
        try { fileSize = new FileInfo(path).Length; } catch { }
        Diag.Log($"[videofix3] GrabVideoThumbnail 开始 path='{path}' exists={File.Exists(path)} size={fileSize}");
        IntPtr pUnk = IntPtr.Zero;
        try
        {
            var riid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463d"); // IID_IShellItemImageFactory
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out pUnk);
            Diag.Log($"[videofix3] SHCreateItemFromParsingName hr=0x{hr:X8} pUnk={pUnk}");
            if (hr != 0 || pUnk == IntPtr.Zero)
            {
                Diag.Log("[videofix3] 无法创建 Shell 项（hr!=0 或为空，可能路径/权限问题）");
                return null;
            }
            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(pUnk);
            Diag.Log("[videofix3] 已创建 IShellItemImageFactory RCW");
            try
            {
                SIZE size = new SIZE { cx = 800, cy = 800 };
                int ghr = factory.GetImage(ref size,
                    SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK,
                    out var hbmp);
                Diag.Log($"[videofix3] GetImage hr=0x{ghr:X8} hbmp={hbmp}");
                if (ghr != 0 || hbmp == IntPtr.Zero)
                {
                    Diag.Log("[videofix3] GetImage 失败（系统未能提供缩略图，可能无解码器）");
                    return null;
                }
                try
                {
                    var result = HBitmapToBitmapSource(hbmp);
                    if (result == null)
                        Diag.Log("[videofix3] HBitmapToBitmapSource 返回 null（转换失败）");
                    else
                        Diag.Log($"[videofix3] HBitmapToBitmapSource 成功 {result.PixelWidth}x{result.PixelHeight}");
                    return result;
                }
                finally
                {
                    DeleteObject(hbmp);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[videofix3] GrabVideoThumbnail 异常: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            // 释放 SHCreateItemFromParsingName 返回的原始引用；RCW（factory）已持有自己的引用，互不影响。
            if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
        }
    }

    // 关键：最后一个参数必须用原生 IntPtr，不要直接声明成 out IShellItemImageFactory 并加
    // [MarshalAs(UnmanagedType.Interface)] —— 那种写法在 CLR 把 COM 指针包成托管接口时会抛
    // InvalidCastException（Specified cast is not valid），导致裁剪预览永远取不到帧、回退成黑底。
    // 改成返回原生 IntPtr，再用 Marshal.GetObjectForIUnknown 显式转接口，稳定可靠。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    // ---- HBITMAP → WPF BitmapSource 的健壮转换 ----
    // CreateBitmapSourceFromHBitmap 对 Shell 返回的某些 HBITMAP（设备相关位图 / alpha=0 的 32bpp ARGB）
    // 会直接得到「纯黑」或「透明黑」，在裁剪对话框里就表现为「背景纯黑、看不到预览」。
    // 这里改用 GDI BitBlt 把任意 HBITMAP 拷进 32bpp 的 DIB 段、强制 alpha=255，再写进 WriteableBitmap，
    // 可正确处理设备相关位图与透明通道，根治黑屏。

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int cb, ref BITMAP lp);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>把任意 HBITMAP（含设备相关位图/DDB）可靠地转换为 WPF BitmapSource。
    /// 通过 GDI BitBlt 拷贝进 32bpp DIB 段、强制 alpha=255，避免 CreateBitmapSourceFromHBitmap 的黑屏问题。</summary>
    private static BitmapSource? HBitmapToBitmapSource(IntPtr hbmp)
    {
        BITMAP bmp = new BITMAP();
        int got = GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bmp);
        Diag.Log($"[videofix3] HBitmap: GetObject={got} w={bmp.bmWidth} h={bmp.bmHeight} bpp={bmp.bmBitsPixel} widthBytes={bmp.bmWidthBytes}");
        if (got == 0) return null;
        int w = bmp.bmWidth, h = bmp.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h; // 自顶向下，避免上下颠倒
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = 0; // BI_RGB

        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr ppvBits;
        IntPtr hDib = CreateDIBSection(hdc, ref bi, 0, out ppvBits, IntPtr.Zero, 0);
        if (hDib == IntPtr.Zero) { Diag.Log("[videofix3] CreateDIBSection 失败"); DeleteDC(hdc); return null; }
        Diag.Log($"[videofix3] CreateDIBSection OK hDib={hDib} ppvBits={ppvBits}");
        // 关键：必须把 DIB 段选入目标 DC，BitBlt 才会真正写入 ppvBits；
        // 否则会写进 DC 默认的 1×1 位图，读出来就是全黑。
        IntPtr oldDst = SelectObject(hdc, hDib);
        try
        {
            IntPtr hdcSrc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                IntPtr old = SelectObject(hdcSrc, hbmp);
                bool blt = BitBlt(hdc, 0, 0, w, h, hdcSrc, 0, 0, 0x00CC0020 /*SRCCOPY*/);
                SelectObject(hdcSrc, old);
                Diag.Log($"[videofix3] BitBlt 返回 {blt}");
            }
            finally { DeleteDC(hdcSrc); }

            int stride = w * 4;
            int bytes = stride * h;
            byte[] px = new byte[bytes];
            Marshal.Copy(ppvBits, px, 0, bytes);

            // 强制不透明：Shell 缩略图常是 alpha=0（GDI「不透明」约定），WPF 会当成透明→黑背景
            for (int i = 3; i < bytes; i += 4) px[i] = 255;

            // 采样像素统计：判断结果是「真实画面」还是「全黑」
            int nonBlack = 0, nonWhite = 0, sampled = 0;
            long sum = 0;
            for (int y = 0; y < h; y += Math.Max(1, h / 10))
                for (int x = 0; x < w; x += Math.Max(1, w / 10))
                {
                    int o = (y * stride) + (x * 4);
                    if (o + 2 >= bytes) continue;
                    int b = px[o], g = px[o + 1], r = px[o + 2];
                    sampled++;
                    int lum = r + g + b;
                    sum += lum;
                    if (lum > 24) nonBlack++;        // 不是纯黑
                    if (lum < 3 * 255 - 24) nonWhite++; // 不是纯白
                }
            int avg = sampled > 0 ? (int)(sum / sampled) : 0;
            Diag.Log($"[videofix3] 像素采样: 采样点={sampled} 非黑点={nonBlack} 非白点={nonWhite} 平均亮度={avg} => {(nonBlack == 0 ? "【全黑/几乎全黑】" : "有内容")}");

            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
            wb.Freeze();
            return wb;
        }
        finally { SelectObject(hdc, oldDst); DeleteObject(hDib); DeleteDC(hdc); }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463d")]
    private interface IShellItemImageFactory
    {
        // 返回 HRESULT；int 返回值的 COM 接口方法默认按 PreserveSig 处理（HRESULT 直接返回）。
        int GetImage([In] ref SIZE size, [In] SIIGBF flags, [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF : uint
    {
        SIIGBF_RESIZETOFIT = 0x00000000,
        SIIGBF_BIGGERSIZEOK = 0x00000001,
        SIIGBF_MEMORYONLY = 0x00000002,
        SIIGBF_ICONONLY = 0x00000004,
        SIIGBF_THUMBNAILONLY = 0x00000008,
        SIIGBF_INCACHEONLY = 0x00000010,
    }
}
