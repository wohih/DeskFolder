using System.Windows;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 展开图标显示排列：设置展开面板中图标网格的行数与列数（每文件夹可单独覆盖，也可跟随全局）。
/// 与「折叠图标显示排列」风格一致；确定后保存并即时重排所有窗口。
/// </summary>
public partial class ExpandArrangeWindow : Window
{
    private static SettingsService S => App.Settings;
    private readonly FolderConfig _config;

    public ExpandArrangeWindow(FolderConfig config)
    {
        _config = config;
        InitializeComponent();

        bool useGlobal = !_config.FolderColumns.HasValue && !_config.FolderRows.HasValue;
        UseGlobalCheck.IsChecked = useGlobal;
        RowsBox.Text = (_config.FolderRows ?? S.Data.Rows).ToString();
        ColsBox.Text = (_config.FolderColumns ?? S.Data.Columns).ToString();
        ScrollCombo.SelectedIndex = (_config.FolderExpandScroll ?? S.Data.ExpandScroll) == 1 ? 1 : 0;
        ApplyGlobalState();
    }

    private void ApplyGlobalState()
    {
        bool useGlobal = UseGlobalCheck.IsChecked == true;
        RowsBox.IsEnabled = !useGlobal;
        ColsBox.IsEnabled = !useGlobal;
        if (useGlobal)
        {
            RowsBox.Text = S.Data.Rows.ToString();
            ColsBox.Text = S.Data.Columns.ToString();
            ScrollCombo.SelectedIndex = S.Data.ExpandScroll == 1 ? 1 : 0;
        }
    }

    private void UseGlobal_CheckChanged(object sender, RoutedEventArgs e) => ApplyGlobalState();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        int scroll = ScrollCombo.SelectedIndex == 1 ? 1 : 0;
        if (UseGlobalCheck.IsChecked == true)
        {
            _config.FolderColumns = null;
            _config.FolderRows = null;
            _config.FolderExpandScroll = null; // 跟随全局：清空本文件夹滚动方向覆盖，否则会卡住全局设置
            S.Data.ExpandScroll = scroll;   // 跟随全局：此处同时编辑全局默认滚动方向
        }
        else if (int.TryParse(RowsBox.Text, out int r) && int.TryParse(ColsBox.Text, out int c)
                 && r >= 1 && c >= 1)
        {
            _config.FolderRows = Math.Clamp(r, 1, 12);
            _config.FolderColumns = Math.Clamp(c, 1, 12);
            _config.FolderExpandScroll = scroll; // 单独覆盖：保存本文件夹滚动方向
        }
        else
        {
            DialogResult = false;
            Close();
            return;
        }

        S.Save();
        S.NotifyChanged(); // 让所有窗口按新的展开行列重排（展开中的本文件夹会即时调整大小）
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
