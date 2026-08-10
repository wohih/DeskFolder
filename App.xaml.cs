using System.IO;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局兜底：任何 UI 线程未处理异常都弹出错误并写入日志，避免直接"闪退"丢失现场
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.Exception}\n\n");
            }
            catch { /* 忽略日志写入失败 */ }
            MessageBox.Show(
                $"发生未处理的错误：\n{ex.Exception.Message}\n\n（详细堆栈已记录到 crash.log）",
                "DeskFolder 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
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
