using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskFolder.Models;

namespace DeskFolder.Services;

/// <summary>
/// .lnk 解析 + 图标提取。
/// 用 WScript.Shell COM 解析快捷方式（无第三方依赖），
/// 用 SHGetFileInfo 提取与资源管理器一致的图标。
/// </summary>
public static class ShortcutService
{
    /// <summary>获取当前用户桌面 + 公共桌面上的所有 .lnk 文件</summary>
    public static IEnumerable<string> GetDesktopLinks()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.lnk"))
                if (seen.Add(file)) yield return file;
        }
    }

    /// <summary>解析一个 .lnk 文件为 ShortcutItem（含图标）</summary>
    public static ShortcutItem? Resolve(string linkPath, bool loadIcon = true)
    {
        if (!File.Exists(linkPath)) return null;
        try
        {
            var item = new ShortcutItem
            {
                LinkPath = linkPath,
                Name = Path.GetFileNameWithoutExtension(linkPath)
            };

            // WScript.Shell COM（动态调用，避免互操作程序集，减小体积）
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(linkPath);
                item.TargetPath = Convert.ToString(shortcut.TargetPath) ?? "";
                item.Arguments = Convert.ToString(shortcut.Arguments) ?? "";
                item.WorkingDirectory = Convert.ToString(shortcut.WorkingDirectory) ?? "";
            }

            if (loadIcon)
                item.Icon = ExtractIcon(item);

            return item;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>启动快捷方式指向的程序</summary>
    public static void Launch(ShortcutItem item)
    {
        try
        {
            var target = item.TargetPath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) && !Directory.Exists(target))
                target = item.LinkPath; // 兜底：直接让 shell 打开 .lnk

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                Arguments = item.Arguments,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                psi.WorkingDirectory = item.WorkingDirectory;
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* 目标程序不存在等情况，静默忽略 */ }
    }

    // ---------------- 图标提取 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000; // 32x32
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private static BitmapSource? ExtractIcon(ShortcutItem item)
    {
        // 优先用目标程序自身的图标，其次用 .lnk 文件的图标
        var candidates = new[] { item.TargetPath, item.LinkPath };
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var exists = File.Exists(path) || Directory.Exists(path);
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!exists)
            {
                // 目标不存在时仍尝试按文件属性取图标
                flags |= SHGFI_USEFILEATTRIBUTES;
            }
            var res = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) continue;

            try
            {
                // 释放图标关联的 GDI 位图，避免句柄泄漏
                if (GetIconInfo(info.hIcon, out var ii))
                {
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                }
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze(); // 跨线程安全 + 降低内存
                return bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        return null;
    }
}
