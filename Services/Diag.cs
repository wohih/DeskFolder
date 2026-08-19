using System;
using System.IO;

namespace DeskFolder.Services;

/// <summary>
/// 临时诊断日志：仅用于排查「视频裁剪预览黑屏」问题，发布后可通过删除此文件关闭。
/// 日志写入 %TEMP%\DeskFolder_video_diag.log。
/// </summary>
internal static class Diag
{
    public const string Version = "videofix3";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "DeskFolder_video_diag.log");

    public static void Reset()
    {
        try { File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DeskFolder 视频诊断日志 {Version} 启动 ===\n"); }
        catch { }
    }

    public static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
            File.AppendAllText(LogPath, line);
        }
        catch { }
    }

    public static string LogFile => LogPath;
}
