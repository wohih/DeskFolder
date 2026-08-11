using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskFolder.Services;

namespace DeskFolder.Views;

public partial class SettingsWindow : Window
{
    private static SettingsService S => App.Settings;
    private bool _suppress;
    /// <summary>若为 null = 全局外观设置（含动画 / 文件夹管理）；
    /// 否则为「单独设置此文件夹外观」模式，仅展示主题列表并应用到指定文件夹。</summary>
    private readonly FolderConfig? _scopeFolder;

    public SettingsWindow(FolderConfig? folder = null)
    {
        _scopeFolder = folder;
        InitializeComponent();

        if (_scopeFolder != null)
        {
            // 进入「单文件夹外观」模式：隐藏全局专属分区，只保留主题选择
            Title = "外观设置 · " + _scopeFolder.Name;
            GlobalSizeSection.Visibility = Visibility.Collapsed;
            AnimSection.Visibility = Visibility.Collapsed;
            FolderMgmtSection.Visibility = Visibility.Collapsed;
            BottomButtons.Visibility = Visibility.Collapsed;
            ThemeHint.Text = "左键点击主题名即可单独应用「此文件夹」的外观；右键点击主题名可详细编辑主题。";
        }

        LoadValues();

        AnimSlider.ValueChanged += (_, _) => AnimValue.Text = ((int)AnimSlider.Value).ToString();
        HoverSlider.ValueChanged += (_, _) => HoverValue.Text = ((int)HoverSlider.Value).ToString();
    }

    private void LoadValues()
    {
        var d = S.Data;
        AnimSlider.Value = d.AnimationMs;
        HoverSlider.Value = d.HoverDelayMs;
        AnimValue.Text = d.AnimationMs.ToString();
        HoverValue.Text = d.HoverDelayMs.ToString();

        ThemeList.ItemsSource = d.Themes;
        _suppress = true;
        // 单文件夹模式：选中该文件夹当前生效的主题（可能是它自己的覆盖，也可能是全局主题）
        ThemeList.SelectedValue = _scopeFolder != null
            ? S.GetThemeForFolder(_scopeFolder.FolderThemeId).Id
            : d.CurrentThemeId;
        _suppress = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var d = S.Data;
        d.AnimationMs = (int)AnimSlider.Value;
        d.HoverDelayMs = (int)HoverSlider.Value;
        S.Save();
        S.NotifyChanged();
        MessageBox.Show(this, "设置已保存并应用。", "DeskFolder",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = S.Data;
        var def = new AppSettingsData();
        d.AnimationMs = def.AnimationMs;
        d.HoverDelayMs = def.HoverDelayMs;
        LoadValues();
    }

    // ---------------- 主题 ----------------

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ThemeList.SelectedItem is not ThemeConfig t) return;
        if (_scopeFolder != null)
        {
            // 单文件夹模式：把该主题绑定到「此文件夹」，仅这一处即时生效（不跟随全局广播）
            _scopeFolder.FolderThemeId = t.Id;
        }
        else
        {
            // 全局模式：设为全局主题并清空所有文件夹的单独覆盖，使全局对所有文件夹生效
            S.SetGlobalTheme(t.Id);
        }
        S.Save();
        S.NotifyChanged(); // 实时切换（目标文件夹窗口即时生效）
    }

    private void ThemeList_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject dep = e.OriginalSource as DependencyObject ?? (DependencyObject)e.Source;
        while (dep != null && !(dep is ListBoxItem))
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem lbi && lbi.DataContext is ThemeConfig t)
        {
            OpenThemeEditor(t);
            e.Handled = true;
        }
    }

    private void OpenThemeEditor(ThemeConfig theme)
    {
        var win = new ThemeEditorWindow(theme, _scopeFolder);
        win.Owner = this;
        win.ShowDialog();
        // 主题名 / 颜色可能已变化，刷新列表（保持当前选中项）
        ThemeList.ItemsSource = null;
        ThemeList.ItemsSource = S.Data.Themes;
        _suppress = true;
        ThemeList.SelectedValue = _scopeFolder != null
            ? S.GetThemeForFolder(_scopeFolder.FolderThemeId).Id
            : S.Data.CurrentThemeId;
        _suppress = false;
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e)
    {
        var src = S.GetCurrentTheme();
        var nt = new ThemeConfig
        {
            Name = "新主题",
            BackgroundColor = src.BackgroundColor,
            BackgroundOpacity = src.BackgroundOpacity,
            CornerRadius = src.CornerRadius
        };
        S.Data.Themes.Add(nt);
        if (_scopeFolder != null)
            _scopeFolder.FolderThemeId = nt.Id;   // 单文件夹模式：新建主题直接应用到此文件夹
        else
            S.SetGlobalTheme(nt.Id);              // 全局模式：新建主题并广播给所有文件夹
        S.Save();
        S.NotifyChanged();
        ThemeList.ItemsSource = null;
        ThemeList.ItemsSource = S.Data.Themes;
        _suppress = true;
        ThemeList.SelectedValue = nt.Id;
        _suppress = false;
        OpenThemeEditor(nt);
    }
}
