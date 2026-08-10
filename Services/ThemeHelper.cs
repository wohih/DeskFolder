using System.Windows.Media;

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
}
