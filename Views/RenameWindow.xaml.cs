using System.Windows;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 重命名当前文件夹：编辑 FolderConfig.Name，确定后同步折叠图标与展开面板标题。
/// </summary>
public partial class RenameWindow : Window
{
    private static SettingsService S => App.Settings;
    private readonly FolderConfig _config;
    private readonly Action? _onSaved;

    public RenameWindow(FolderConfig config, Action? onSaved = null)
    {
        _config = config;
        _onSaved = onSaved;
        InitializeComponent();
        NameBox.Text = _config.Name;
        NameBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = _config.Name;
        _config.Name = name;
        S.Save();
        _onSaved?.Invoke();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
