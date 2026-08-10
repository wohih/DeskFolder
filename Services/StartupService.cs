using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskFolder.Services;

/// <summary>
/// 开机自启动：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 实现。
/// 纯 Win32 P/Invoke，不引入任何 NuGet 依赖。
/// </summary>
internal static class StartupService
{
    private const int HKEY_CURRENT_USER = unchecked((int)0x80000001);
    private const uint KEY_READ = 0x20019;
    private const uint KEY_SET_VALUE = 0x0002;
    private const uint REG_SZ = 1;
    private const int ERROR_SUCCESS = 0;

    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskFolder";

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int RegOpenKeyEx(int hKey, string lpSubKey, int ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, StringBuilder? lpData, ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int reserved, uint dwType, string lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int RegDeleteValue(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    /// <summary>当前是否已在注册表 Run 中注册开机自启动。</summary>
    public static bool IsEnabled()
    {
        if (RegOpenKeyEx(HKEY_CURRENT_USER, RunSubKey, 0, KEY_READ, out IntPtr hKey) != ERROR_SUCCESS)
            return false;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            uint type = 0;
            int r = RegQueryValueEx(hKey, ValueName, IntPtr.Zero, out type, sb, ref size);
            return r == ERROR_SUCCESS && type == REG_SZ && sb.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>启用 / 禁用开机自启动（写入 / 删除 Run 键值，值为当前 exe 完整路径）。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (RegOpenKeyEx(HKEY_CURRENT_USER, RunSubKey, 0, KEY_SET_VALUE, out IntPtr hKey) != ERROR_SUCCESS)
            return;
        try
        {
            if (enabled)
            {
                string? exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                // cbData 为字节数，含结尾空字符（Unicode 每个字符 2 字节）
                RegSetValueEx(hKey, ValueName, 0, REG_SZ, exe, (exe.Length + 1) * 2);
            }
            else
            {
                RegDeleteValue(hKey, ValueName);
            }
        }
        catch
        {
            // 注册表写失败（极少见，如权限不足）不影响主流程
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }
}
