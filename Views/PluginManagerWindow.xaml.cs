using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 插件管理窗口：为指定文件夹挂载 / 编辑 / 移除装饰性插件（时钟 / 便签 / 日历等）。
/// 与主题无关，任何主题下都可启用，用于桌面美化装饰。
/// </summary>
public partial class PluginManagerWindow : Window
{
    private static SettingsService S => App.Settings;
    private readonly FolderConfig _folder;
    private bool _suppress;

    /// <summary>显示用包装类：把 FolderPlugin 加一个友好显示名，方便 ListBox 展示。</summary>
    private class PluginDisplay
    {
        public FolderPlugin Plugin { get; set; } = new();
        public string DisplayName => Plugin.Type switch
        {
            FolderPluginType.AnalogClock => "模拟时钟",
            FolderPluginType.DigitalClock => "数字时钟",
            FolderPluginType.StickyNote => $"便签：{(string.IsNullOrWhiteSpace(Plugin.Text) ? "（空）" : Plugin.Text)}",
            FolderPluginType.CpuGauge => "CPU 仪表盘",
            FolderPluginType.WeatherBadge => $"天气：{(string.IsNullOrWhiteSpace(Plugin.Text) ? "24°C" : Plugin.Text)}",
            FolderPluginType.CalendarTile => "日历方块",
            FolderPluginType.MusicPlayer => "音乐播放器（酷狗音乐）",
            _ => "未配置"
        };
    }

    public PluginManagerWindow(FolderConfig folder)
    {
        InitializeComponent();
        _folder = folder;
        FolderNameLabel.Text = "文件夹：" + folder.Name;
        LoadList();
    }

    private void LoadList()
    {
        _suppress = true;
        PluginList.Items.Clear();
        if (_folder.Plugins != null)
        {
            foreach (var p in _folder.Plugins)
            {
                var pd = new PluginDisplay { Plugin = p };
                PluginList.Items.Add(pd);
            }
        }
        _suppress = false;
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void AddPluginBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_folder.Plugins == null) _folder.Plugins = new();
        var p = new FolderPlugin
        {
            Type = FolderPluginType.AnalogClock,
            ShowOnCollapsed = true,
            ShowOnExpanded = false,
            CollapsedCorner = 1,
            ExpandedCorner = 0,
            Size = 48,
            Color = "#333333"
        };
        _folder.Plugins.Add(p);
        var pd = new PluginDisplay { Plugin = p };
        PluginList.Items.Add(pd);
        PluginList.SelectedItem = pd;
        S.Save();
    }

    private void PluginList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (PluginList.SelectedItem is not PluginDisplay pd)
        {
            EditPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EditPanel.Visibility = Visibility.Visible;
        LoadEditPanel(pd.Plugin);
    }

    private void LoadEditPanel(FolderPlugin p)
    {
        _suppress = true;
        TypeCombo.SelectedIndex = (int)p.Type - 1; // 枚举 None=0 跳过，从 AnalogClock=1 开始
        CollapsedCornerCombo.SelectedIndex = Math.Clamp(p.CollapsedCorner, 0, 3);
        ExpandedCornerCombo.SelectedIndex = Math.Clamp(p.ExpandedCorner, 0, 3);
        GridSizeCombo.SelectedIndex = (int)p.GridSize;
        ShowCollapsedCheck.IsChecked = p.ShowOnCollapsed;
        ShowExpandedCheck.IsChecked = p.ShowOnExpanded;
        SizeSlider.Value = p.Size;
        SizeVal.Text = p.Size == 0 ? "默认" : p.Size.ToString();
        CollapsedOffsetXBox.Text = p.CollapsedOffsetX.ToString();
        CollapsedOffsetYBox.Text = p.CollapsedOffsetY.ToString();
        ExpandedOffsetXBox.Text = p.ExpandedOffsetX.ToString();
        ExpandedOffsetYBox.Text = p.ExpandedOffsetY.ToString();
        TextBox.Text = p.Text ?? "";
        ColorBox.Text = p.Color ?? "";
        UpdateColorPreview(p.Color);
        LyricFontSlider.Value = p.LyricFontSize;
        LyricFontVal.Text = p.LyricFontSize == 0 ? "自动" : p.LyricFontSize.ToString();
        _suppress = false;
    }

    private void UpdateColorPreview(string? hex)
    {
        if (ThemeHelper.TryParseColor(hex ?? "", out var c))
            ColorPreview.Background = new SolidColorBrush(c);
    }

    private void TypeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.Type = (FolderPluginType)(TypeCombo.SelectedIndex + 1);
        // 刷新列表项显示
        int idx = PluginList.SelectedIndex;
        LoadList();
        if (idx >= 0 && idx < PluginList.Items.Count)
        {
            PluginList.SelectedIndex = idx;
            if (PluginList.SelectedItem is PluginDisplay pd2) LoadEditPanel(pd2.Plugin);
        }
        S.Save();
    }

    private void CollapsedCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.CollapsedCorner = CollapsedCornerCombo.SelectedIndex;
        S.Save();
    }

    private void ExpandedCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.ExpandedCorner = ExpandedCornerCombo.SelectedIndex;
        S.Save();
    }

    private void GridSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.GridSize = (PluginGridSize)GridSizeCombo.SelectedIndex;
        // 重置网格位置，让系统自动分配
        pd.Plugin.GridRow = -1;
        pd.Plugin.GridColumn = -1;
        S.Save();
    }

    private void ShowCollapsed_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.ShowOnCollapsed = ShowCollapsedCheck.IsChecked == true;
        S.Save();
    }

    private void ShowExpanded_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.ShowOnExpanded = ShowExpandedCheck.IsChecked == true;
        S.Save();
    }

    private void SizeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.Size = (int)SizeSlider.Value;
        SizeVal.Text = pd.Plugin.Size == 0 ? "默认" : pd.Plugin.Size.ToString();
        S.Save();
    }

    private static bool TryParseDouble(TextBox box, out double val)
    {
        if (double.TryParse(box.Text, out val)) return true;
        val = 0;
        return false;
    }

    private void CollapsedOffsetX_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        if (TryParseDouble(CollapsedOffsetXBox, out var v)) { pd.Plugin.CollapsedOffsetX = v; S.Save(); }
    }
    private void CollapsedOffsetY_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        if (TryParseDouble(CollapsedOffsetYBox, out var v)) { pd.Plugin.CollapsedOffsetY = v; S.Save(); }
    }
    private void ExpandedOffsetX_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        if (TryParseDouble(ExpandedOffsetXBox, out var v)) { pd.Plugin.ExpandedOffsetX = v; S.Save(); }
    }
    private void ExpandedOffsetY_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        if (TryParseDouble(ExpandedOffsetYBox, out var v)) { pd.Plugin.ExpandedOffsetY = v; S.Save(); }
    }

    private void Text_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.Text = TextBox.Text;
        // 刷新列表显示
        int idx = PluginList.SelectedIndex;
        LoadList();
        if (idx >= 0 && idx < PluginList.Items.Count)
        {
            _suppress = true;
            PluginList.SelectedIndex = idx;
            _suppress = false;
        }
        S.Save();
    }

    private void Color_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.Color = ColorBox.Text;
        UpdateColorPreview(pd.Plugin.Color);
        S.Save();
    }

    private void LyricFontSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || PluginList.SelectedItem is not PluginDisplay pd) return;
        pd.Plugin.LyricFontSize = (int)LyricFontSlider.Value;
        LyricFontVal.Text = pd.Plugin.LyricFontSize == 0 ? "自动" : pd.Plugin.LyricFontSize.ToString();
        S.Save();
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginDisplay pd) return;
        _folder.Plugins?.Remove(pd.Plugin);
        LoadList();
        S.Save();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
