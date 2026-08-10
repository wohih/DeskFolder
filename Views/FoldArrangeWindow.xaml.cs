using System.Windows;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 折叠图标显示排列：设置折叠图标内预览缩略图的行数与列数（1-6），确定后保存并重排所有窗口。
/// </summary>
public partial class FoldArrangeWindow : Window
{
    private static SettingsService S => App.Settings;

    public FoldArrangeWindow()
    {
        InitializeComponent();
        RowsBox.Text = S.Data.PreviewRows.ToString();
        ColsBox.Text = S.Data.PreviewCols.ToString();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(RowsBox.Text, out int r) && int.TryParse(ColsBox.Text, out int c)
            && r >= 1 && c >= 1)
        {
            S.Data.PreviewRows = Math.Clamp(r, 1, 6);
            S.Data.PreviewCols = Math.Clamp(c, 1, 6);
            S.Save();
            S.NotifyChanged(); // 让所有窗口按新的预览行列重排
            DialogResult = true;
            Close();
        }
        else
        {
            // 输入非法：关闭且不保存
            DialogResult = false;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
