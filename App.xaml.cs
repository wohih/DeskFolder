using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DeskFolder.Services;
using DeskFolder.Views;

namespace DeskFolder;

public partial class App : System.Windows.Application
{
    private TrayIcon? _tray;
    private readonly List<FolderWindow> _windows = new();
    private SettingsWindow? _settingsWindow;

    public static SettingsService Settings { get; private set; } = null!;

    // crash.log 写入目录（exe同级+AppData双写，确保用户能找到）
    private static string CrashLogDir
    {
        get
        {
            try
            {
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName)
                             ?? AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(exeDir);
                return exeDir;
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
            }
        }
    }

    private static void WriteCrashLog(Exception ex, string source)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"===== [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ({source}) =====");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine();
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace ?? "(no stack trace)");
            // 递归 InnerException
            int depth = 0;
            var inner = ex.InnerException;
            while (inner != null && depth < 10)
            {
                depth++;
                sb.AppendLine();
                sb.AppendLine($"--- InnerException #{depth} ---");
                sb.AppendLine($"Message: {inner.Message}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
                inner = inner.InnerException;
            }
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            // 写入 exe 同级目录 + AppData 双写
            string logFile = Path.Combine(CrashLogDir, "crash.log");
            File.AppendAllText(logFile, sb.ToString());

            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
            Directory.CreateDirectory(appDataDir);
            File.AppendAllText(Path.Combine(appDataDir, "crash.log"), sb.ToString());
        }
        catch
        {
            // 日志写入本身失败则彻底忽略
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局兜底：UI 线程未处理异常（静默写入日志，不弹窗，避免阻塞 UI）
        DispatcherUnhandledException += (_, ex) =>
        {
            WriteCrashLog(ex.Exception, "DispatcherUnhandledException");
            ex.Handled = true;
        };

        // 非 UI 线程异常兜底
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc)
                WriteCrashLog(exc, "AppDomain.UnhandledException");
        };

        // Task 未观察异常兜底
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            WriteCrashLog(ex.Exception, "TaskScheduler.UnobservedTaskException");
            ex.SetObserved();
        };

        Settings = SettingsService.Load();

        // 首次运行：自动把桌面上的快捷方式导入默认文件夹
        if (Settings.Data.Folders.Count == 0)
        {
            var folder = new FolderConfig { Name = "桌面应用" };
            foreach (var lnk in ShortcutService.GetDesktopLinks())
                folder.Shortcuts.Add(lnk);
            Settings.Data.Folders.Add(folder);
            Settings.Save();
        }

        Settings.SettingsChanged += ApplySettings;
        // 保证注册表 Run 项与设置中"开机自启动"一致
        Services.StartupService.SetEnabled(Settings.Data.LaunchAtStartup);
        CreateTray();
        OpenAllFolders();

        // 启动期有大量瞬态分配（JSON 反序列化、COM RCW、图标解码中间对象），
        // 启动完成后主动回收一次，释放堆碎片，降低空闲驻留内存
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private void OpenAllFolders()
    {
        foreach (var win in _windows.ToList()) win.Close();
        _windows.Clear();
        foreach (var folder in Settings.Data.Folders)
        {
            var win = new FolderWindow(folder);
            win.Show();
            _windows.Add(win);
        }
    }

    /// <summary>新建一个空白文件夹窗口</summary>
    public void AddFolder()
    {
        var folder = new FolderConfig { Name = $"新建文件夹 {Settings.Data.Folders.Count + 1}" };
        Settings.Data.Folders.Add(folder);
        Settings.Save();
        var win = new FolderWindow(folder);
        win.Show();
        _windows.Add(win);
    }

    /// <summary>删除指定文件夹：从配置与窗口列表中移除并关闭其窗口（不删除桌面 .lnk 文件）</summary>
    public void DeleteFolder(FolderWindow win)
    {
        Settings.Data.Folders.Remove(win.Config);
        Settings.Save();
        _windows.Remove(win);
        win.Close();
    }

    private void ApplySettings()
    {
        // 设置变更：折叠尺寸 / 展开行列 / 动画等即时重排生效
        foreach (var win in _windows) win.RefreshLayout();
    }

    private void CreateTray()
    {
        _tray = new TrayIcon(
            tip: "DeskFolder 桌面文件夹",
            onSettings: ShowSettings,
            onRearrange: OpenAllFolders,
            onExit: Quit,
            isAutoStart: () => Settings.Data.LaunchAtStartup,
            onToggleAutoStart: ToggleAutoStart,
            onNewFolder: AddFolder);
    }

    /// <summary>切换"开机自启动"：翻转设置、落盘、并同步注册表 Run 项。</summary>
    private void ToggleAutoStart()
    {
        bool next = !Settings.Data.LaunchAtStartup;
        Settings.Data.LaunchAtStartup = next;
        Settings.Save();
        Services.StartupService.SetEnabled(next);
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void Quit()
    {
        Settings.Save();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
