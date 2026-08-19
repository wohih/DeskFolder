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
            ThemeHint.Text = "左键点击主题名即可单独应用「此文件夹」的外观（自动复制为专属副本，与其它文件夹互不影响）；右键点击主题名可详细编辑主题。";
        }
        else
        {
            // 全局模式：仅保留动画 / 排列等全局项，移除「外观/主题」设置（外观须按文件夹单独设置）
            ThemeCard.Visibility = Visibility.Collapsed;
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

        // 主题列表仅在「单文件夹外观」模式下加载（全局模式已隐藏外观分区）
        if (_scopeFolder != null)
            RefreshThemeList();
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
        if (_scopeFolder == null) return; // 全局模式不再处理主题
        if (ThemeList.SelectedItem is not ThemeConfig t) return;
        if (t.Id == _scopeFolder.FolderThemeId) return; // 选中当前副本，无需处理
        // 克隆为专属副本，保证与其它文件夹互不影响（即使都曾选过同一主题）
        var clone = S.CloneTheme(t);
        S.Data.Themes.Add(clone);
        CleanupOrphanTheme(_scopeFolder.FolderThemeId);
        _scopeFolder.FolderThemeId = clone.Id;
        S.Save();
        S.NotifyChanged(); // 实时切换（仅当前文件夹窗口生效）
        RefreshThemeList();
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
        ThemeConfig target = theme;
        if (_scopeFolder != null && theme.BuiltInId != null)
        {
            // 编辑内置模板前先克隆为专属副本，避免改动模板影响其它文件夹
            var clone = S.CloneTheme(theme);
            S.Data.Themes.Add(clone);
            CleanupOrphanTheme(_scopeFolder.FolderThemeId);
            _scopeFolder.FolderThemeId = clone.Id;
            S.Save();
            S.NotifyChanged();
            target = clone;
        }
        var win = new ThemeEditorWindow(target, _scopeFolder);
        win.Owner = this;
        win.ShowDialog();
        RefreshThemeList();
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_scopeFolder == null) return; // 全局模式无主题
        var baseTheme = S.GetThemeForFolder(_scopeFolder.FolderThemeId);
        var nt = S.CloneTheme(baseTheme);
        nt.Name = "新主题";
        S.Data.Themes.Add(nt);
        CleanupOrphanTheme(_scopeFolder.FolderThemeId);
        _scopeFolder.FolderThemeId = nt.Id;
        S.Save();
        S.NotifyChanged();
        RefreshThemeList();
        OpenThemeEditor(nt);
    }

    /// <summary>刷新主题列表为「内置模板 + 本文件夹当前专属副本」，并高亮当前项。</summary>
    private void RefreshThemeList()
    {
        if (_scopeFolder == null) return;
        _suppress = true;
        var list = S.Data.Themes.Where(t => t.BuiltInId != null).ToList();
        var cur = S.Data.Themes.FirstOrDefault(t => t.Id == _scopeFolder.FolderThemeId);
        if (cur != null && cur.BuiltInId == null) list.Add(cur); // 显示本文件夹自身的专属副本，便于再次编辑
        ThemeList.ItemsSource = list;
        ThemeList.SelectedValue = _scopeFolder.FolderThemeId;
        _suppress = false;
    }

    /// <summary>若旧主题是非内置、且不再被任何其它文件夹引用，则从主题库中移除（避免克隆体不断堆积）。</summary>
    private void CleanupOrphanTheme(string? oldId)
    {
        if (string.IsNullOrEmpty(oldId)) return;
        var old = S.Data.Themes.FirstOrDefault(t => t.Id == oldId);
        if (old == null || old.BuiltInId != null) return; // 内置模板始终保留
        bool referencedElsewhere = S.Data.Folders.Any(f => f != _scopeFolder && f.FolderThemeId == oldId);
        if (!referencedElsewhere) S.Data.Themes.Remove(old);
    }
}
