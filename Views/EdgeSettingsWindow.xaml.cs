using System.Windows;
using DeskFolder.Models;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 贴边设置（仅「贴边文件夹」主题可用）：
/// 调整贴边位置（顶 / 左 / 右）、折叠方框透明度 / 宽高 / 远离屏幕的圆角、展开时距屏幕边缘的距离。
/// 修改即时反映到所属文件夹窗口（实时预览），确定后落盘保存。
/// </summary>
public partial class EdgeSettingsWindow : Window
{
    private static SettingsService S => App.Settings;
    private readonly FolderConfig _config;
    private FolderWindow? _owner;

    // 取消时用于还原（实时预览会直接修改共享的 _config 引用）
    private readonly int _origAnchor;
    private readonly double _origOpacity, _origW, _origH, _origCorner, _origDist;

    // 贴边位置 Combo 项 → EdgeAnchor 值：0=顶, 1=左, 2=右
    private static int AnchorFromIndex(int idx) => idx switch
    {
        0 => 1, // 顶边
        1 => 2, // 左边
        _ => 3  // 右边
    };

    public EdgeSettingsWindow(FolderConfig config)
    {
        _config = config;
        InitializeComponent();

        AnchorCombo.SelectedIndex = _config.EdgeAnchor switch
        {
            1 => 0,
            3 => 2,
            _ => 1 // 左（默认）
        };
        OpacitySlider.Value = ThemeHelper.Clamp(_config.EdgeBoxOpacity, 0, 1);
        OpacityText.Text = $"{(int)(OpacitySlider.Value * 100)}%";
        WidthBox.Text = ((int)Math.Max(20, _config.EdgeBoxWidth)).ToString();
        HeightBox.Text = ((int)Math.Max(20, _config.EdgeBoxHeight)).ToString();
        CornerSlider.Value = ThemeHelper.Clamp(_config.EdgeBoxCorner, 0, 40);
        CornerText.Text = ((int)CornerSlider.Value).ToString();
        DistanceBox.Text = ((int)Math.Max(0, _config.EdgeDistance)).ToString();

        // 记录原始值，供「取消」时还原
        _origAnchor = _config.EdgeAnchor;
        _origOpacity = _config.EdgeBoxOpacity;
        _origW = _config.EdgeBoxWidth;
        _origH = _config.EdgeBoxHeight;
        _origCorner = _config.EdgeBoxCorner;
        _origDist = _config.EdgeDistance;
    }

    private void Preview()
    {
        _owner ??= Owner as FolderWindow;
        _owner?.RefreshEdgeVisual();
    }

    private void AnchorCombo_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _config.EdgeAnchor = AnchorFromIndex(AnchorCombo.SelectedIndex);
        Preview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _config.EdgeBoxOpacity = ThemeHelper.Clamp(OpacitySlider.Value, 0, 1);
        OpacityText.Text = $"{(int)(_config.EdgeBoxOpacity * 100)}%";
        Preview();
    }

    private void SizeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (int.TryParse(WidthBox.Text, out int w) && w >= 20)
            _config.EdgeBoxWidth = w;
        if (int.TryParse(HeightBox.Text, out int h) && h >= 20)
            _config.EdgeBoxHeight = h;
        Preview();
    }

    private void CornerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _config.EdgeBoxCorner = ThemeHelper.Clamp(CornerSlider.Value, 0, 40);
        CornerText.Text = ((int)_config.EdgeBoxCorner).ToString();
        Preview();
    }

    private void DistanceBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (int.TryParse(DistanceBox.Text, out int d) && d >= 0)
            _config.EdgeDistance = d;
        Preview();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 最终把当前配置落盘
        S.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 取消：还原实时预览对共享 _config 的改动，再关闭（调用方 Closed 中会按还原值重新渲染）
        _config.EdgeAnchor = _origAnchor;
        _config.EdgeBoxOpacity = _origOpacity;
        _config.EdgeBoxWidth = _origW;
        _config.EdgeBoxHeight = _origH;
        _config.EdgeBoxCorner = _origCorner;
        _config.EdgeDistance = _origDist;
        DialogResult = false;
        Close();
    }
}
