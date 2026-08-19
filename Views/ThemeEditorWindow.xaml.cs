using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>
/// 单个主题的详细设置：名称、类型（填充颜色 / 简约方框 / 图片背景）。
/// 填充颜色：背景颜色（H/S/V + 十六进制，范围不限）+ 背景透明度 + 圆角。
/// 简约方框：方框颜色 + 方框宽度 + 方框类型 + 圆角（完全透明背景，仅边框）。
/// 图片背景：导入图片或视频（png/jpg/bmp/gif/tif/wmp/ico 及 mp4/wmv/avi/mov/m4v/mkv 等；
/// 视频静音循环播放，GIF 等动图自动播放）+ 图片/视频透明度 + 圆角。
/// 若正在编辑"当前主题"，所有改动实时反映到桌面的文件夹窗口。
/// </summary>
public partial class ThemeEditorWindow : Window
{
    private readonly ThemeConfig _theme;
    private readonly FolderConfig? _folder; // 文件夹级裁剪配置的目标；null 时裁剪配置存到主题上
    private double _h, _s, _v;   // 色相 0-360，饱和度/明度 0-100（贴合滑块范围）
    private double _th, _ts, _tv; // 文字颜色 H/S/V（独立状态）
    private bool _suppress;

    // 裁剪辅助：当前文件夹折叠 / 展开尺寸（像素），用于在裁剪对话框中绘制两种状态的取景辅助框
    private readonly double _collW, _collH, _expW, _expH;

    // 切换间隔预设（分钟），与 XAML 中 Interval 下拉项顺序一致
    private static readonly int[] IntervalPresets = { 1, 3, 5, 10, 15, 30, 60 };

    private static SettingsService S => App.Settings;

    /// <summary>
    /// 编辑单个主题。folder 不为 null 时，按该文件夹的实际折叠 / 展开尺寸计算裁剪取景辅助框，
    /// 且裁剪配置保存到文件夹（不影响其他使用同一主题的文件夹）；
    /// folder 为 null 时使用默认折叠尺寸与按全局行列估算的展开尺寸（当前外观已改为仅文件夹单独设置，folder 一般不为 null）。
    /// </summary>
    public ThemeEditorWindow(ThemeConfig theme, FolderConfig? folder = null)
    {
        _theme = theme;
        _folder = folder;

        // 折叠尺寸：文件夹覆盖优先，否则默认 150×150
        _collW = folder?.FolderFoldW ?? 150;
        _collH = folder?.FolderFoldH ?? 150;
        // 展开尺寸：必须与 FolderWindow.RecomputeTargets 完全一致（行数=设定行列，图标溢出走滚动条，不再撑大面板）——
        // 旧代码曾按图标数向上取整多出行，但 RecomputeTargets 已改为固定行列、溢出滚动，
        // 那样会让编辑器展开的宽高比比真实面板更"高"，导致裁剪后填充比例不符、桌面显示偏移。
        // 容器 = 图标内容网格（IconCell=75）+ 面板内边距（PanelPaddingH=44 / V=38）+ 顶部标题栏（HeaderHeight=34）。
        int cols = Math.Max(1, folder?.FolderColumns ?? S.Data.Columns);
        int rows = Math.Max(1, folder?.FolderRows ?? S.Data.Rows);
        _expW = cols * 75 + 44;
        _expH = rows * 75 + 34 + 38;

        InitializeComponent();

        NameBox.Text = _theme.Name;
        ModeCombo.SelectedIndex = (int)_theme.Mode;
        SyncModeButtons((int)_theme.Mode); // 同步分段控件按钮外观
        OpacitySlider.Value = _theme.BackgroundOpacity * 100;
        RadiusSlider.Value = _theme.CornerRadius;
        BorderWidthSlider.Value = _theme.BorderThickness;

        BorderStyleCombo.ItemsSource = ThemeHelper.BorderStyleNames;
        BorderStyleCombo.SelectedIndex = _theme.BorderStyle;

        // 文字设置控件初始化
        InitTextControls();

        // 颜色选择器目标（填充=背景色，方框=方框色），载入到 H/S/V
        LoadColorFromTarget();

        // 色相条静态彩虹背景
        HueSlider.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Colors.Red, 0),
            new GradientStop(Colors.Yellow, 0.167),
            new GradientStop(Colors.Lime, 0.333),
            new GradientStop(Colors.Cyan, 0.5),
            new GradientStop(Colors.Blue, 0.667),
            new GradientStop(Colors.Magenta, 0.833),
            new GradientStop(Colors.Red, 1)
        }, new Point(0, 0), new Point(1, 0));

        _suppress = true;
        HueSlider.Value = _h;
        SatSlider.Value = _s;
        ValSlider.Value = _v;
        _suppress = false;

        HueSlider.ValueChanged += (_, _) => { if (!_suppress) { _h = HueSlider.Value; FromHsv(); } };
        SatSlider.ValueChanged += (_, _) => { if (!_suppress) { _s = SatSlider.Value; FromHsv(); } };
        ValSlider.ValueChanged += (_, _) => { if (!_suppress) { _v = ValSlider.Value; FromHsv(); } };
        OpacitySlider.ValueChanged += (_, _) =>
        {
            _theme.BackgroundOpacity = OpacitySlider.Value / 100.0;
            OpacityVal.Text = ((int)OpacitySlider.Value) + "%";
            RefreshVisuals();
            Commit();
        };
        RadiusSlider.ValueChanged += (_, _) =>
        {
            _theme.CornerRadius = RadiusSlider.Value;
            RadiusVal.Text = ((int)RadiusSlider.Value) + "px";
            RefreshVisuals();
            Commit();
        };
        BorderWidthSlider.ValueChanged += (_, _) =>
        {
            _theme.BorderThickness = BorderWidthSlider.Value;
            BorderWidthVal.Text = ((int)BorderWidthSlider.Value) + "px";
            RefreshVisuals();
            Commit();
        };

        DeleteButton.Visibility = _theme.BuiltInId == null ? Visibility.Visible : Visibility.Collapsed;

        InitAdvancedParamsUI();
        ShowGroupsForMode(_theme.Mode);
        InitImageModeUI();
        RefreshVisuals();
        UpdateCropStatus();
    }

    private void SetPreviewBrush(UIElement e, string hex)
    {
        if (e is Border b && ThemeHelper.TryParseColor(hex, out var c))
            b.Background = new SolidColorBrush(c);
    }

    /// <summary>点击颜色预览时聚焦对应的 hex 输入框并全选文字，方便直接键入新颜色值。</summary>
    private static void FocusAndSelectHex(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    /// <summary>初始化渐变 / 霓虹 / 玻璃 / 亚克力 / 折纸 / 浮雕 6 种高级主题的参数控件。</summary>
    private void InitAdvancedParamsUI()
    {
        // ---- 渐变 ----
        _suppress = true;
        SetPreviewBrush(GradientColorAPreview, _theme.GradientColorA);
        GradientColorABox.Text = _theme.GradientColorA;
        SetPreviewBrush(GradientColorBPreview, _theme.GradientColorB);
        GradientColorBBox.Text = _theme.GradientColorB;
        GradientTypeCombo.SelectedIndex = Math.Clamp(_theme.GradientType, 0, 4);
        GradientAngleSlider.Value = _theme.GradientAngle;
        GradientAngleVal.Text = ((int)GradientAngleSlider.Value).ToString() + "°";
        GradientAngleRow.Visibility = _theme.GradientType == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- 霓虹 ----
        SetPreviewBrush(NeonGlowPreview, _theme.NeonGlowColor);
        NeonGlowBox.Text = _theme.NeonGlowColor;
        SetPreviewBrush(NeonBgPreview, _theme.NeonBgColor);
        NeonBgBox.Text = _theme.NeonBgColor;
        NeonGlowIntensitySlider.Value = _theme.NeonGlowIntensity;
        NeonGlowIntensityVal.Text = NeonGlowIntensitySlider.Value.ToString("0.00") + "x";

        // ---- 玻璃 ----
        SetPreviewBrush(GlassTintPreview, _theme.GlassTintColor);
        GlassTintBox.Text = _theme.GlassTintColor;
        SetPreviewBrush(GlassHlPreview, _theme.GlassHighlight);
        GlassHlBox.Text = _theme.GlassHighlight;
        GlassSatSlider.Value = _theme.GlassSaturation;
        GlassSatVal.Text = GlassSatSlider.Value.ToString("0.00");

        // ---- 亚克力 ----
        SetPreviewBrush(AcrylicTintPreview, _theme.AcrylicTint);
        AcrylicTintBox.Text = _theme.AcrylicTint;
        AcrylicOpacitySlider.Value = _theme.AcrylicOpacity;
        AcrylicOpacityVal.Text = AcrylicOpacitySlider.Value.ToString("0.00");
        AcrylicNoiseSlider.Value = _theme.AcrylicNoise;
        AcrylicNoiseVal.Text = AcrylicNoiseSlider.Value.ToString("0.00");

        // ---- 折纸 ----
        SetPreviewBrush(PaperColorPreview, _theme.PaperColor);
        PaperColorBox.Text = _theme.PaperColor;
        PaperFoldCombo.SelectedIndex = Math.Clamp(_theme.PaperFoldDirection, 0, 3);
        PaperShadowSlider.Value = _theme.PaperShadowDepth;
        PaperShadowVal.Text = PaperShadowSlider.Value.ToString("0.00") + "x";

        // ---- 浮雕 ----
        SetPreviewBrush(EmbossColorPreview, _theme.EmbossColor);
        EmbossColorBox.Text = _theme.EmbossColor;
        EmbossHeightSlider.Value = _theme.EmbossHeight;
        EmbossHeightVal.Text = EmbossHeightSlider.Value.ToString("0.0") + "px";
        _suppress = false;
    }

    /// <summary>颜色选择器当前编辑的目标颜色（填充→BackgroundColor，方框→BorderColor）。</summary>
    private string TargetColor
    {
        get => _theme.Mode == ThemeMode.BorderOnly ? _theme.BorderColor : _theme.BackgroundColor;
        set { if (_theme.Mode == ThemeMode.BorderOnly) _theme.BorderColor = value; else _theme.BackgroundColor = value; }
    }

    /// <summary>将当前目标颜色解析到 H/S/V 并同步滑块。</summary>
    private void LoadColorFromTarget()
    {
        if (ThemeHelper.TryParseColor(TargetColor, out var c))
            ThemeHelper.RgbToHsv(c, out _h, out _s, out _v);
        _s *= 100; _v *= 100;
        _suppress = true;
        HueSlider.Value = Math.Clamp(_h, 0, 360);
        SatSlider.Value = Math.Clamp(_s, 0, 100);
        ValSlider.Value = Math.Clamp(_v, 0, 100);
        _suppress = false;
    }

    /// <summary>根据主题类型显示 / 隐藏各设置分组及其卡片容器。</summary>
    private void ShowGroupsForMode(ThemeMode mode)
    {
        bool color = mode is ThemeMode.Fill or ThemeMode.BorderOnly;
        bool opacity = mode is ThemeMode.Fill or ThemeMode.Image or ThemeMode.Gradient or ThemeMode.Neon;
        ColorGroup.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
        OpacityCard.Visibility = opacity ? Visibility.Visible : Visibility.Collapsed;
        BorderCard.Visibility = mode == ThemeMode.BorderOnly ? Visibility.Visible : Visibility.Collapsed;
        ImageCard.Visibility = mode == ThemeMode.Image ? Visibility.Visible : Visibility.Collapsed;
        RadiusGroup.Visibility = Visibility.Visible;

        GradientCard.Visibility = mode == ThemeMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
        NeonCard.Visibility = mode == ThemeMode.Neon ? Visibility.Visible : Visibility.Collapsed;
        GlassCard.Visibility = mode == ThemeMode.Glass ? Visibility.Visible : Visibility.Collapsed;
        AcrylicCard.Visibility = mode == ThemeMode.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        PaperCard.Visibility = mode == ThemeMode.Paper ? Visibility.Visible : Visibility.Collapsed;
        EmbossCard.Visibility = mode == ThemeMode.Emboss ? Visibility.Visible : Visibility.Collapsed;

        ColorLabel.Text = mode == ThemeMode.BorderOnly ? "方框颜色" : "背景颜色";
        OpacityLabel.Text = mode switch
        {
            ThemeMode.Image => "图片透明度",
            ThemeMode.Neon => "背景透明度",
            ThemeMode.Gradient => "渐变透明度",
            _ => "背景透明度"
        };
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _theme.Mode = (ThemeMode)ModeCombo.SelectedIndex;
        SyncModeButtons(ModeCombo.SelectedIndex); // 同步分段按钮
        ShowGroupsForMode(_theme.Mode);
        LoadColorFromTarget();
        RefreshVisuals();
        Commit();
    }

    /// <summary>初始化文字设置相关控件（字体列表 / 大小 / 颜色 / 位置 / 隐藏）。</summary>
    private void InitTextControls()
    {
        // 字体下拉：默认字体 + 常用字体族 + 艺术字体（装饰性字体族，按系统已安装为准）
        FontCombo.Items.Add(new ComboBoxItem { Content = "默认字体", Tag = "" });
        foreach (var f in new[]
        {
            // 中文常用
            "Microsoft YaHei UI", "微软雅黑", "微软雅黑 Light", "SimSun", "SimHei", "KaiTi", "STKaiti", "STXinwei", "STXingkai", "STLiti", "STZhongsong", "FZShuTi", "YouYuan", "LiSu",
            // 西文 / 艺术字体
            "Arial", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS",
            "Brush Script MT", "Britannic Bold", "Cooper Black", "Elephant", "Forte",
            "Freestyle Script", "Gigi", "Ink Free", "Jokerman", "Magneto", "Mistral",
            "Papyrus", "Ravie", "Script MT Bold", "Showcard Gothic", "Snap ITC", "Vivaldi",
            "Wide Latin", "Blackadder ITC", "Bauhaus 93", "Bernard MT Condensed"
        })
            FontCombo.Items.Add(new ComboBoxItem { Content = f, Tag = f });
        FontCombo.SelectedIndex = IndexOfTag(FontCombo, _theme.TextFont);

        TextSizeSlider.Value = _theme.TextSize;
        TextSizeVal.Text = _theme.TextSize > 0 ? ((int)_theme.TextSize) + "px" : "默认";

        bool auto = string.IsNullOrWhiteSpace(_theme.TextColor);
        TextColorAuto.IsChecked = auto;
        TextColorPanel.IsEnabled = !auto;
        if (!auto)
        {
            TextColorHex.Text = _theme.TextColor;
            SetTextColorSwatch(_theme.TextColor);
        }
        else
        {
            // 自动模式下预置白色，取消自动后即可作为初始值
            TextColorHex.Text = "#FFFFFF";
        }

        // 快速取色：常用颜色一键选
        foreach (var qc in new[]
        {
            "#FFFFFF", "#000000", "#FF0000", "#FFA500", "#FFFF00", "#00FF00",
            "#00FFFF", "#0000FF", "#800080", "#FF69B4", "#808080", "#A52A2A"
        })
        {
            var btn = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(qc)),
                BorderBrush = new SolidColorBrush(Colors.Gray), BorderThickness = new Thickness(1),
                ToolTip = qc, Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) => SetTextColor(qc);
            TextQuickColors.Children.Add(btn);
        }

        // 色相条静态彩虹背景
        TextHueSlider.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Colors.Red, 0),
            new GradientStop(Colors.Yellow, 0.167),
            new GradientStop(Colors.Lime, 0.333),
            new GradientStop(Colors.Cyan, 0.5),
            new GradientStop(Colors.Blue, 0.667),
            new GradientStop(Colors.Magenta, 0.833),
            new GradientStop(Colors.Red, 1)
        }, new Point(0, 0), new Point(1, 0));

        // 将当前文字颜色载入 H/S/V 滑块
        LoadTextColorToHsv();

        // 拖动滑块即合成颜色
        TextHueSlider.ValueChanged += (_, _) => { if (!_suppress) { _th = TextHueSlider.Value; FromTextHsv(); } };
        TextSatSlider.ValueChanged += (_, _) => { if (!_suppress) { _ts = TextSatSlider.Value; FromTextHsv(); } };
        TextValSlider.ValueChanged += (_, _) => { if (!_suppress) { _tv = TextValSlider.Value; FromTextHsv(); } };

        TextPosCombo.SelectedIndex = Math.Clamp(_theme.TextPosition, 0, 2);

        BoldCheck.IsChecked = _theme.TextBold;

        HideCollapsedCheck.IsChecked = _theme.HideTextCollapsed;
        HideExpandedCheck.IsChecked = _theme.HideTextExpanded;
        HideShortcutNamesCheck.IsChecked = _theme.HideShortcutNames;
        HideIconCollapsedCheck.IsChecked = _theme.HideIconCollapsed;

        // 实时联动（拖动即生效）
        TextSizeSlider.ValueChanged += (_, _) =>
        {
            _theme.TextSize = TextSizeSlider.Value;
            TextSizeVal.Text = _theme.TextSize > 0 ? ((int)_theme.TextSize) + "px" : "默认";
            RefreshVisuals();
            Commit();
        };
    }

    private static int IndexOfTag(ItemsControl ctrl, string tag)
    {
        for (int i = 0; i < ctrl.Items.Count; i++)
            if (ctrl.Items[i] is ComboBoxItem ci && (string?)ci.Tag == tag) return i;
        return 0;
    }

    private void SetTextColorSwatch(string hex)
    {
        if (ThemeHelper.TryParseColor(hex, out var c))
            TextColorSwatch.Background = new SolidColorBrush(c);
        else
            TextColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>将当前文字颜色解析到 H/S/V 并同步滑块（自动模式用预置白色初始化）。</summary>
    private void LoadTextColorToHsv()
    {
        if (ThemeHelper.TryParseColor(_theme.TextColor, out var c))
            ThemeHelper.RgbToHsv(c, out _th, out _ts, out _tv);
        else
        {
            _th = 0; _ts = 0; _tv = 1; // 默认白（H/S/V 为 0..1 刻度，下方 ×100 转滑块刻度）
        }
        _ts *= 100; _tv *= 100;
        _suppress = true;
        // 夹取后再赋值：滑块有 Maximum 限制，越界赋值会抛异常使整个窗口构造失败（表现为"点开没反应 + 闪退"）
        TextHueSlider.Value = Math.Clamp(_th, 0, 360);
        TextSatSlider.Value = Math.Clamp(_ts, 0, 100);
        TextValSlider.Value = Math.Clamp(_tv, 0, 100);
        TextHVal.Text = ((int)_th).ToString() + "°";
        TextSVal.Text = ((int)_ts).ToString() + "%";
        TextVVal.Text = ((int)_tv).ToString() + "%";
        _suppress = false;
    }

    /// <summary>由当前文字 H/S/V 合成颜色，写回主题，并刷新色块与预览。</summary>
    private void FromTextHsv()
    {
        var color = ThemeHelper.HsvToRgb(_th, _ts / 100.0, _tv / 100.0);
        _theme.TextColor = ThemeHelper.ToHex(color);
        _suppress = true;
        TextColorHex.Text = _theme.TextColor;
        _suppress = false;
        SetTextColorSwatch(_theme.TextColor);
        TextHVal.Text = ((int)_th).ToString() + "°";
        TextSVal.Text = ((int)_ts).ToString() + "%";
        TextVVal.Text = ((int)_tv).ToString() + "%";
        RefreshVisuals();
        Commit();
    }

    /// <summary>由快速取色或外部代码直接设定文字颜色（同步 hex / 色块 / HSV 滑块）。</summary>
    private void SetTextColor(string hex)
    {
        if (!ThemeHelper.TryParseColor(hex, out _)) return;
        _theme.TextColor = hex;
        _suppress = true;
        TextColorHex.Text = hex;
        _suppress = false;
        SetTextColorSwatch(hex);
        LoadTextColorToHsv();
        RefreshVisuals();
        Commit();
    }

    private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is ComboBoxItem ci)
            _theme.TextFont = (string?)ci.Tag ?? "";
        RefreshVisuals();
        Commit();
    }

    private void TextColorAuto_Changed(object sender, RoutedEventArgs e)
    {
        bool auto = TextColorAuto.IsChecked == true;
        TextColorPanel.IsEnabled = !auto;
        if (auto)
        {
            _theme.TextColor = "";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TextColorHex.Text))
                TextColorHex.Text = "#FFFFFF";
            _theme.TextColor = TextColorHex.Text;
            SetTextColorSwatch(_theme.TextColor);
            LoadTextColorToHsv();
        }
        RefreshVisuals();
        Commit();
    }

    private void TextColorHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TextColorAuto.IsChecked == true) return;
        if (!ThemeHelper.TryParseColor(TextColorHex.Text, out _))
        {
            SetTextColorSwatch("#000000");
            return;
        }
        _theme.TextColor = TextColorHex.Text;
        SetTextColorSwatch(_theme.TextColor);
        LoadTextColorToHsv();
        RefreshVisuals();
        Commit();
    }

    private void TextPosCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextPosCombo.SelectedIndex < 0) return;
        _theme.TextPosition = TextPosCombo.SelectedIndex;
        RefreshVisuals();
        Commit();
    }

    private void BoldCheck_Changed(object sender, RoutedEventArgs e)
    {
        _theme.TextBold = BoldCheck.IsChecked == true;
        RefreshVisuals();
        Commit();
    }

    private void HideCollapsed_Changed(object sender, RoutedEventArgs e)
    {
        _theme.HideTextCollapsed = HideCollapsedCheck.IsChecked == true;
        RefreshVisuals();
        Commit();
    }

    private void HideExpanded_Changed(object sender, RoutedEventArgs e)
    {
        _theme.HideTextExpanded = HideExpandedCheck.IsChecked == true;
        RefreshVisuals();
        Commit();
    }

    private void HideShortcutNames_Changed(object sender, RoutedEventArgs e)
    {
        _theme.HideShortcutNames = HideShortcutNamesCheck.IsChecked == true;
        RefreshVisuals();
        Commit();
    }

    private void HideIconCollapsed_Changed(object sender, RoutedEventArgs e)
    {
        _theme.HideIconCollapsed = HideIconCollapsedCheck.IsChecked == true;
        RefreshVisuals();
        Commit();
    }

    private void BorderStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BorderStyleCombo.SelectedIndex < 0) return;
        _theme.BorderStyle = BorderStyleCombo.SelectedIndex;
        RefreshVisuals();
        Commit();
    }

    /// <summary>由当前 H/S/V 合成颜色，写回主题，并刷新所有视觉控件。</summary>
    private void FromHsv()
    {
        var color = ThemeHelper.HsvToRgb(_h, _s / 100.0, _v / 100.0);
        TargetColor = ThemeHelper.ToHex(color);

        _suppress = true;
        HexBox.Text = TargetColor;
        _suppress = false;

        HVal.Text = ((int)_h).ToString() + "°";
        SVal.Text = ((int)_s).ToString() + "%";
        VVal.Text = ((int)_v).ToString() + "%";

        // 饱和度/明度条的渐变随色相更新，提供直观反馈
        SatSlider.Background = new LinearGradientBrush(Colors.Gray, color, 0);
        ValSlider.Background = new LinearGradientBrush(Colors.Black, color, 0);

        RefreshVisuals();
        Commit();
    }

    /// <summary>刷新预览色块 / 示例文件夹 / 文字对比色。</summary>
    private void RefreshVisuals()
    {
        if (_theme.Mode == ThemeMode.BorderOnly)
        {
            ThemeHelper.TryParseColor(_theme.BorderColor, out var bc);
            ColorPreview.Background = new SolidColorBrush(bc);
            SampleChip.Background = Brushes.Transparent;
            SampleChip.BorderBrush = new SolidColorBrush(bc);
            SampleChip.BorderThickness = new Thickness(_theme.BorderThickness);
            SampleChip.CornerRadius = new CornerRadius(_theme.CornerRadius);
            SampleText.Foreground = new SolidColorBrush(ThemeHelper.ContrastColor(bc));
            SampleText.Effect = null;
            ImageThumb.Source = null;
        }
        else if (_theme.Mode == ThemeMode.Image)
        {
            ColorPreview.Background = Brushes.Transparent;
            SampleChip.Background = Brushes.Transparent;
            SampleChip.BorderThickness = new Thickness(0);
            SampleChip.CornerRadius = new CornerRadius(_theme.CornerRadius);
            SampleText.Foreground = Brushes.White;
            SampleText.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 3, Opacity = 0.8, ShadowDepth = 0 };
            string? rep = RepresentativeImagePath();
            if (!string.IsNullOrWhiteSpace(rep) && File.Exists(rep))
                ImageThumb.Source = LoadCroppedImage(rep);
            else ImageThumb.Source = null;
        }
        else // Fill
        {
            ThemeHelper.TryParseColor(_theme.BackgroundColor, out var c);
            byte a = (byte)ThemeHelper.Clamp(_theme.BackgroundOpacity * 255, 0, 255);
            var bg = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            ColorPreview.Background = bg;
            SampleChip.Background = bg;
            SampleChip.BorderThickness = new Thickness(0);
            SampleChip.CornerRadius = new CornerRadius(_theme.CornerRadius);
            SampleText.Foreground = new SolidColorBrush(ThemeHelper.ContrastColor(c));
            SampleText.Effect = null;
            ImageThumb.Source = null;
        }

        // 文字设置预览：字体 / 大小 / 颜色（自动时沿用上面的对比/白字）
        SampleText.FontFamily = string.IsNullOrWhiteSpace(_theme.TextFont)
            ? new FontFamily("Microsoft YaHei UI")
            : new FontFamily(_theme.TextFont);
        SampleText.FontSize = _theme.TextSize > 0 ? _theme.TextSize : 14;
        SampleText.FontWeight = _theme.TextBold ? FontWeights.Bold : FontWeights.Normal;
        if (ThemeHelper.TryParseColor(_theme.TextColor, out var tcol))
            SampleText.Foreground = new SolidColorBrush(tcol);
    }

    /// <summary>持久化；若该主题正被使用（全局当前主题或被任一文件夹引用）则实时应用到桌面窗口。
    /// 当 _folder 不为 null 时（文件夹级设置），强制通知所有窗口刷新（因为裁剪配置可能已变更）。</summary>
    private void Commit()
    {
        S.Save();
        if (_folder != null || S.IsThemeInUse(_theme.Id))
            S.NotifyChanged();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _theme.Name = NameBox.Text;
        S.Save();
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (!ThemeHelper.TryParseColor(HexBox.Text, out var c)) return;
        ThemeHelper.RgbToHsv(c, out _h, out _s, out _v);
        _s *= 100; _v *= 100;
        _suppress = true;
        HueSlider.Value = _h;
        SatSlider.Value = _s;
        ValSlider.Value = _v;
        _suppress = false;
        FromHsv();
    }

    // ---------------- 图片导入 / 列表 / 播放设置 ----------------

    /// <summary>把选中的多张图片追加进指定图片组（去重，按路径忽略大小写）。</summary>
    private void ImportInto(ImagePlaylist playlist)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择文件夹背景图片 / 视频",
            Filter = "图片/视频文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.wmp;*.ico;" +
                     "*.mp4;*.wmv;*.avi;*.mov;*.m4v;*.mkv;*.webm;*.mpg;*.mpeg)|" +
                     "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.wmp;*.ico;" +
                     "*.mp4;*.wmv;*.avi;*.mov;*.m4v;*.mkv;*.webm;*.mpg;*.mpeg|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (!playlist.Paths.Contains(f, StringComparer.OrdinalIgnoreCase))
                playlist.Paths.Add(f);
        RenderAllPools();
        RefreshVisuals();
        Commit();
    }

    private void SingleImport_Click(object sender, RoutedEventArgs e) => ImportInto(_theme.Single);
    private void CollapsedImport_Click(object sender, RoutedEventArgs e) => ImportInto(_theme.Collapsed);
    private void ExpandedImport_Click(object sender, RoutedEventArgs e) => ImportInto(_theme.Expanded);

    /// <summary>初始化图片模式相关控件：模式单选、各播放方式/间隔下拉、面板显隐与列表。</summary>
    private void InitImageModeUI()
    {
        SingleModeRadio.IsChecked = _theme.ImageLayout == ImageLayoutMode.Single;
        MultiModeRadio.IsChecked = _theme.ImageLayout == ImageLayoutMode.Multi;
        SinglePlayCombo.SelectedIndex = (int)_theme.Single.Play;
        CollapsedPlayCombo.SelectedIndex = (int)_theme.Collapsed.Play;
        ExpandedPlayCombo.SelectedIndex = (int)_theme.Expanded.Play;
        SingleIntervalCombo.SelectedIndex = IndexOfInterval(_theme.Single.IntervalMinutes);
        CollapsedIntervalCombo.SelectedIndex = IndexOfInterval(_theme.Collapsed.IntervalMinutes);
        ExpandedIntervalCombo.SelectedIndex = IndexOfInterval(_theme.Expanded.IntervalMinutes);
        ShowImagePanels();
        RenderAllPools();
    }

    /// <summary>按当前 ImageLayout 显示/隐藏单图面板与多图面板。</summary>
    private void ShowImagePanels()
    {
        bool single = _theme.ImageLayout == ImageLayoutMode.Single;
        SinglePanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        MultiPanel.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ImgLayout_Changed(object sender, RoutedEventArgs e)
    {
        // 防御：InitializeComponent 解析 XAML 设置 RadioButton.IsChecked 会提前触发本事件，
        // 此时 SinglePanel/MultiPanel 可能尚未被赋值（字段为 null），直接引用会 NullReferenceException ——
        // 表现为"右键主题设置没反应 + 闪退"。构造完成后由 InitImageModeUI 显式调用 ShowImagePanels，故此处可安全跳过。
        if (SinglePanel == null || MultiPanel == null) return;
        _theme.ImageLayout = SingleModeRadio.IsChecked == true
            ? ImageLayoutMode.Single : ImageLayoutMode.Multi;
        ShowImagePanels();
        Commit();
    }

    /// <summary>把三个图片组的缩略图列表重绘（含文件名与移除按钮）。</summary>
    private void RenderAllPools()
    {
        RenderPoolList(SingleList, _theme.Single);
        RenderPoolList(CollapsedList, _theme.Collapsed);
        RenderPoolList(ExpandedList, _theme.Expanded);
    }

    /// <summary>绘制单个图片组的缩略图列表：每项含 36×36 缩略图 + 文件名 + 移除按钮。</summary>
    private void RenderPoolList(Panel host, ImagePlaylist playlist)
    {
        host.Children.Clear();
        for (int i = 0; i < playlist.Paths.Count; i++)
        {
            string path = playlist.Paths[i];
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center
            };
            var thumb = new Image { Width = 36, Height = 36, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 4, 0) };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 72; // 缩略图 36×36，2 倍解码足够清晰且省内存
                bmp.EndInit();
                bmp.Freeze();
                thumb.Source = bmp;
            }
            catch { }
            // 视频文件无法用 BitmapImage 解码：回退到统一的视频占位缩略图
            if (thumb.Source == null && ThemeHelper.IsVideoFile(path))
                thumb.Source = ThemeHelper.VideoThumbPlaceholder();
            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                FontSize = 11,
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var del = new Button
            {
                Content = "✕",
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "移除",
                Cursor = System.Windows.Input.Cursors.Hand
            };
            int idx = i;
            del.Click += (_, _) =>
            {
                playlist.Paths.RemoveAt(idx);
                RenderAllPools();
                RefreshVisuals();
                Commit();
            };
            item.Children.Add(thumb);
            item.Children.Add(name);
            item.Children.Add(del);
            host.Children.Add(item);
        }
        if (playlist.Paths.Count == 0)
            host.Children.Add(new TextBlock
            {
                Text = "（尚未导入图片）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Gray)
            });
    }

    /// <summary>把间隔分钟数映射到下拉项索引（找不到则用默认的 5 分钟）。</summary>
    private static int IndexOfInterval(int minutes)
    {
        for (int i = 0; i < IntervalPresets.Length; i++)
            if (IntervalPresets[i] == minutes) return i;
        return 2; // 5 分钟
    }

    private void SinglePlay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SinglePlayCombo.SelectedIndex < 0) return;
        _theme.Single.Play = (ImagePlayMode)SinglePlayCombo.SelectedIndex;
        Commit();
    }
    private void CollapsedPlay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CollapsedPlayCombo.SelectedIndex < 0) return;
        _theme.Collapsed.Play = (ImagePlayMode)CollapsedPlayCombo.SelectedIndex;
        Commit();
    }
    private void ExpandedPlay_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExpandedPlayCombo.SelectedIndex < 0) return;
        _theme.Expanded.Play = (ImagePlayMode)ExpandedPlayCombo.SelectedIndex;
        Commit();
    }

    private void SingleInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SingleIntervalCombo.SelectedIndex < 0) return;
        _theme.Single.IntervalMinutes = IntervalPresets[SingleIntervalCombo.SelectedIndex];
        Commit();
    }
    private void CollapsedInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CollapsedIntervalCombo.SelectedIndex < 0) return;
        _theme.Collapsed.IntervalMinutes = IntervalPresets[CollapsedIntervalCombo.SelectedIndex];
        Commit();
    }
    private void ExpandedInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ExpandedIntervalCombo.SelectedIndex < 0) return;
        _theme.Expanded.IntervalMinutes = IntervalPresets[ExpandedIntervalCombo.SelectedIndex];
        Commit();
    }

    /// <summary>读取图片并按裁剪区域（优先文件夹级，其次主题）返回裁剪后的 BitmapSource，用于缩略图预览。</summary>
    private BitmapSource? LoadCroppedImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            // 优先使用文件夹级折叠态裁剪配置
            bool hasCrop = _folder != null ? HasCrop(false) : _theme.HasImageCrop;
            if (hasCrop)
            {
                double cx = _folder != null ? GetCropVal(false, "X")!.Value : _theme.ImageCropX!.Value;
                double cy = _folder != null ? GetCropVal(false, "Y")!.Value : _theme.ImageCropY!.Value;
                double cw = _folder != null ? GetCropVal(false, "W")!.Value : _theme.ImageCropW!.Value;
                double ch = _folder != null ? GetCropVal(false, "H")!.Value : _theme.ImageCropH!.Value;
                int x = (int)Math.Round(cx * bmp.PixelWidth);
                int y = (int)Math.Round(cy * bmp.PixelHeight);
                int w = (int)Math.Round(cw * bmp.PixelWidth);
                int h = (int)Math.Round(ch * bmp.PixelHeight);
                x = Math.Max(0, Math.Min(x, bmp.PixelWidth - 1));
                y = Math.Max(0, Math.Min(y, bmp.PixelHeight - 1));
                w = Math.Max(1, Math.Min(w, bmp.PixelWidth - x));
                h = Math.Max(1, Math.Min(h, bmp.PixelHeight - y));
                var cb = new CroppedBitmap(bmp, new Int32Rect(x, y, w, h));
                cb.Freeze();
                return cb;
            }
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>取得用于缩略图/裁剪预览的代表图片：单图模式取共用组首图；多图模式优先折叠态组。</summary>
    private string? RepresentativeImagePath()
    {
        static string? First(ImagePlaylist p) => p.Paths.Count > 0 ? p.Paths[0] : null;
        if (_theme.ImageLayout == ImageLayoutMode.Single)
            return First(_theme.Single) ?? First(_theme.Collapsed) ?? First(_theme.Expanded);
        return First(_theme.Collapsed) ?? First(_theme.Expanded) ?? First(_theme.Single);
    }

    private void CropImage_Click(object sender, RoutedEventArgs e) => OpenCrop(0);
    private void CropCollapsed_Click(object sender, RoutedEventArgs e) => OpenCrop(1);
    private void CropExpanded_Click(object sender, RoutedEventArgs e) => OpenCrop(2);

    // ---------------- 裁剪配置：支持文件夹级覆盖 ----------------
    // 当 _folder 不为 null 时，裁剪配置保存到 FolderConfig（不影响其他使用同一主题的文件夹）；
    // 当 _folder 为 null 时，裁剪配置保存到 ThemeConfig（当前外观已改为仅文件夹单独设置，_folder 一般不为 null）。
    // 读取时优先使用文件夹级配置，如没有则回退到主题配置。

    private bool HasCrop(bool expanded) =>
        _folder != null
            ? (expanded ? _folder.HasFolderImageCropExpanded : _folder.HasFolderImageCrop)
                || (expanded ? _theme.HasImageCropExpanded : _theme.HasImageCrop)
            : expanded ? _theme.HasImageCropExpanded : _theme.HasImageCrop;

    private double? GetCropVal(bool expanded, string which)
    {
        if (_folder != null)
        {
            double? val = which switch
            {
                "X" => expanded ? _folder.FolderImageCropExpandedX : _folder.FolderImageCropX,
                "Y" => expanded ? _folder.FolderImageCropExpandedY : _folder.FolderImageCropY,
                "W" => expanded ? _folder.FolderImageCropExpandedW : _folder.FolderImageCropW,
                "H" => expanded ? _folder.FolderImageCropExpandedH : _folder.FolderImageCropH,
                _ => null
            };
            if (val.HasValue) return val;
        }
        return which switch
        {
            "X" => expanded ? _theme.ImageCropExpandedX : _theme.ImageCropX,
            "Y" => expanded ? _theme.ImageCropExpandedY : _theme.ImageCropY,
            "W" => expanded ? _theme.ImageCropExpandedW : _theme.ImageCropW,
            "H" => expanded ? _theme.ImageCropExpandedH : _theme.ImageCropH,
            _ => null
        };
    }

    private void SetCropVal(bool expanded, string which, double? value)
    {
        if (_folder != null)
        {
            switch (which)
            {
                case "X": if (expanded) _folder.FolderImageCropExpandedX = value; else _folder.FolderImageCropX = value; break;
                case "Y": if (expanded) _folder.FolderImageCropExpandedY = value; else _folder.FolderImageCropY = value; break;
                case "W": if (expanded) _folder.FolderImageCropExpandedW = value; else _folder.FolderImageCropW = value; break;
                case "H": if (expanded) _folder.FolderImageCropExpandedH = value; else _folder.FolderImageCropH = value; break;
            }
        }
        else
        {
            switch (which)
            {
                case "X": if (expanded) _theme.ImageCropExpandedX = value; else _theme.ImageCropX = value; break;
                case "Y": if (expanded) _theme.ImageCropExpandedY = value; else _theme.ImageCropY = value; break;
                case "W": if (expanded) _theme.ImageCropExpandedW = value; else _theme.ImageCropW = value; break;
                case "H": if (expanded) _theme.ImageCropExpandedH = value; else _theme.ImageCropH = value; break;
            }
        }
    }

    private void ClearCropVals(bool expanded)
    {
        SetCropVal(expanded, "X", null);
        SetCropVal(expanded, "Y", null);
        SetCropVal(expanded, "W", null);
        SetCropVal(expanded, "H", null);
    }

    /// <summary>打开裁剪对话框；editState：0=同时编辑折叠/展开两态，1=仅折叠，2=仅展开（另态保持原值）。</summary>
    private void OpenCrop(int editState)
    {
        string? path = editState switch
        {
            2 => _theme.Expanded.Paths.Count > 0 ? _theme.Expanded.Paths[0] : null,
            1 => _theme.Collapsed.Paths.Count > 0 ? _theme.Collapsed.Paths[0] : null,
            _ => _theme.Single.Paths.Count > 0 ? _theme.Single.Paths[0] : null
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "请先导入至少一张图片，再裁剪。", "裁剪图片",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Rect? coll = HasCrop(false)
            ? new Rect(GetCropVal(false, "X")!.Value, GetCropVal(false, "Y")!.Value,
                       GetCropVal(false, "W")!.Value, GetCropVal(false, "H")!.Value)
            : null;
        Rect? exp = HasCrop(true)
            ? new Rect(GetCropVal(true, "X")!.Value, GetCropVal(true, "Y")!.Value,
                       GetCropVal(true, "W")!.Value, GetCropVal(true, "H")!.Value)
            : null;
        var win = new ImageCropWindow(path, coll, exp, _collW, _collH, _expW, _expH, editState, _folder?.Name);
        win.Owner = this;
        if (win.ShowDialog() != true) return;

        if (editState == 1)
        {
            var c = win.CollapsedCrop;
            SetCropVal(false, "X", c?.X); SetCropVal(false, "Y", c?.Y);
            SetCropVal(false, "W", c?.Width); SetCropVal(false, "H", c?.Height);
        }
        else if (editState == 2)
        {
            var x = win.ExpandedCrop;
            SetCropVal(true, "X", x?.X); SetCropVal(true, "Y", x?.Y);
            SetCropVal(true, "W", x?.Width); SetCropVal(true, "H", x?.Height);
        }
        else
        {
            // 同时编辑两态
            var c = win.CollapsedCrop;
            SetCropVal(false, "X", c?.X); SetCropVal(false, "Y", c?.Y);
            SetCropVal(false, "W", c?.Width); SetCropVal(false, "H", c?.Height);
            var x = win.ExpandedCrop;
            SetCropVal(true, "X", x?.X); SetCropVal(true, "Y", x?.Y);
            SetCropVal(true, "W", x?.Width); SetCropVal(true, "H", x?.Height);
        }

        RefreshVisuals();
        UpdateCropStatus();
        Commit();
    }

    /// <summary>清除裁剪区域（折叠态与展开态一并清除，恢复显示整图）。</summary>
    private void ClearCrop_Click(object sender, RoutedEventArgs e) => ClearCrop(true);

    /// <summary>清除裁剪区域；refresh 为 true 时同步刷新预览与状态（导入换图时复用，避免重复刷新）。</summary>
    private void ClearCrop(bool refresh)
    {
        ClearCropVals(false);
        ClearCropVals(true);
        if (refresh)
        {
            RefreshVisuals();
            UpdateCropStatus();
            Commit();
        }
    }

    /// <summary>更新裁剪状态文案与缩略图下方提示（折叠态 / 展开态分别显示）。</summary>
    private void UpdateCropStatus()
    {
        string CollState() => HasCrop(false) ? "已裁剪" : "整图";
        string ExpState() => HasCrop(true) ? "已裁剪" : "整图";
        CropStatusText.Text = $"折叠态：{CollState()}　展开态：{ExpState()}";
        ClearCropBtn.IsEnabled = HasCrop(false) || HasCrop(true);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_theme.BuiltInId != null) return;
        if (MessageBox.Show("确定删除该主题吗？", "删除主题",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        // 任何引用了被删主题的文件夹：重新绑定到默认「半透明」专属副本，避免丢失外观（不再回退到全局）
        foreach (var f in S.Data.Folders)
        {
            if (f.FolderThemeId == _theme.Id)
            {
                var fallback = S.CloneTheme(S.Data.Themes.First(t => t.BuiltInId == "semi"));
                S.Data.Themes.Add(fallback);
                f.FolderThemeId = fallback.Id;
            }
        }
        S.Data.Themes.Remove(_theme);
        S.Save();
        S.NotifyChanged();
        DialogResult = true;
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        S.Save();
        DialogResult = true;
        Close();
    }

    // ---------------- 自定义标题栏 / 分段控件 ----------------

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        S.Save();
        DialogResult = true;
        Close();
    }

    /// <summary>分段控件：选中填充颜色模式。</summary>
    private void ModeFill_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        ModeCombo.SelectedIndex = 0;
        _suppress = false;
        SyncModeButtons(0);
        ShowGroupsForMode(ThemeMode.Fill);
        LoadColorFromTarget();
        RefreshVisuals();
        Commit();
    }

    /// <summary>分段控件：选中简约方框模式。</summary>
    private void ModeBorder_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        ModeCombo.SelectedIndex = 1;
        _suppress = false;
        SyncModeButtons(1);
        ShowGroupsForMode(ThemeMode.BorderOnly);
        LoadColorFromTarget();
        RefreshVisuals();
        Commit();
    }

    /// <summary>分段控件：选中图片背景模式。</summary>
    private void ModeImage_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        ModeCombo.SelectedIndex = 2;
        _suppress = false;
        SyncModeButtons(2);
        ShowGroupsForMode(ThemeMode.Image);
        LoadColorFromTarget();
        RefreshVisuals();
        Commit();
    }

    /// <summary>同步三个分段按钮的视觉状态（选中=蓝底白字，未选=灰底深字）。</summary>
    private void SyncModeButtons(int selectedIndex)
    {
        var buttons = new[] { ModeFillBtn, ModeBorderBtn, ModeImageBtn, ModeGradientBtn, ModeNeonBtn, ModeGlassBtn, ModeAcrylicBtn, ModePaperBtn, ModeEmbossBtn };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i == selectedIndex)
            {
                buttons[i].Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
                buttons[i].Foreground = Brushes.White;
            }
            else
            {
                buttons[i].Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
                buttons[i].Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            }
        }
    }

    private void ModeGradient_Click(object sender, RoutedEventArgs e) { SetMode(3); }
    private void ModeNeon_Click(object sender, RoutedEventArgs e) { SetMode(4); }
    private void ModeGlass_Click(object sender, RoutedEventArgs e) { SetMode(5); }
    private void ModeAcrylic_Click(object sender, RoutedEventArgs e) { SetMode(6); }
    private void ModePaper_Click(object sender, RoutedEventArgs e) { SetMode(7); }
    private void ModeEmboss_Click(object sender, RoutedEventArgs e) { SetMode(8); }

    private void SetMode(int idx)
    {
        _suppress = true;
        ModeCombo.SelectedIndex = idx;
        _suppress = false;
        SyncModeButtons(idx);
        ShowGroupsForMode((ThemeMode)idx);
        LoadColorFromTarget();
        RefreshVisuals();
        Commit();
    }

    // ---------------- 高级主题参数：渐变 / 霓虹 / 玻璃 / 亚克力 / 折纸 / 浮雕 事件 ----------------

    // -- 颜色 Hex 同步工具 --
    private static bool TrySyncColor(TextBox box, Border preview, Action<string> apply, out string res)
    {
        var text = (box.Text ?? "").Trim();
        if (!ThemeHelper.TryParseColor(text, out var c))
        {
            res = text;
            return false;
        }
        text = c.ToString();
        preview.Background = new SolidColorBrush(c);
        apply(text);
        res = text;
        return true;
    }

    // -- 渐变 --
    private void GradientColorA_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(GradientColorABox, GradientColorAPreview, v => _theme.GradientColorA = v, out _);
        RefreshVisuals(); Commit();
    }
    private void GradientColorB_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(GradientColorBBox, GradientColorBPreview, v => _theme.GradientColorB = v, out _);
        RefreshVisuals(); Commit();
    }
    private void GradientColorA_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(GradientColorABox);
    }
    private void GradientColorB_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(GradientColorBBox);
    }
    private void GradientTypeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _theme.GradientType = GradientTypeCombo.SelectedIndex;
        GradientAngleRow.Visibility = _theme.GradientType == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshVisuals(); Commit();
    }
    private void GradientAngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.GradientAngle = GradientAngleSlider.Value;
        GradientAngleVal.Text = ((int)GradientAngleSlider.Value).ToString() + "°";
        RefreshVisuals(); Commit();
    }

    // -- 霓虹 --
    private void NeonGlow_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(NeonGlowBox, NeonGlowPreview, v => _theme.NeonGlowColor = v, out _);
        RefreshVisuals(); Commit();
    }
    private void NeonBg_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(NeonBgBox, NeonBgPreview, v => _theme.NeonBgColor = v, out _);
        RefreshVisuals(); Commit();
    }
    private void NeonGlow_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(NeonGlowBox);
    }
    private void NeonBg_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(NeonBgBox);
    }
    private void NeonGlowIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.NeonGlowIntensity = NeonGlowIntensitySlider.Value;
        NeonGlowIntensityVal.Text = NeonGlowIntensitySlider.Value.ToString("0.00") + "x";
        RefreshVisuals(); Commit();
    }

    // -- 玻璃 --
    private void GlassTint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(GlassTintBox, GlassTintPreview, v => _theme.GlassTintColor = v, out _);
        RefreshVisuals(); Commit();
    }
    private void GlassHl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(GlassHlBox, GlassHlPreview, v => _theme.GlassHighlight = v, out _);
        RefreshVisuals(); Commit();
    }
    private void GlassTint_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(GlassTintBox);
    }
    private void GlassHl_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(GlassHlBox);
    }
    private void GlassSat_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.GlassSaturation = GlassSatSlider.Value;
        GlassSatVal.Text = GlassSatSlider.Value.ToString("0.00");
        RefreshVisuals(); Commit();
    }

    // -- 亚克力 --
    private void AcrylicTint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(AcrylicTintBox, AcrylicTintPreview, v => _theme.AcrylicTint = v, out _);
        RefreshVisuals(); Commit();
    }
    private void AcrylicTint_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(AcrylicTintBox);
    }
    private void AcrylicOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.AcrylicOpacity = AcrylicOpacitySlider.Value;
        AcrylicOpacityVal.Text = AcrylicOpacitySlider.Value.ToString("0.00");
        RefreshVisuals(); Commit();
    }
    private void AcrylicNoise_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.AcrylicNoise = AcrylicNoiseSlider.Value;
        AcrylicNoiseVal.Text = AcrylicNoiseSlider.Value.ToString("0.00");
        RefreshVisuals(); Commit();
    }

    // -- 折纸 --
    private void PaperColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(PaperColorBox, PaperColorPreview, v => _theme.PaperColor = v, out _);
        RefreshVisuals(); Commit();
    }
    private void PaperColor_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(PaperColorBox);
    }
    private void PaperFoldCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _theme.PaperFoldDirection = PaperFoldCombo.SelectedIndex;
        RefreshVisuals(); Commit();
    }
    private void PaperShadow_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.PaperShadowDepth = PaperShadowSlider.Value;
        PaperShadowVal.Text = PaperShadowSlider.Value.ToString("0.00") + "x";
        RefreshVisuals(); Commit();
    }

    // -- 浮雕 --
    private void EmbossColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        TrySyncColor(EmbossColorBox, EmbossColorPreview, v => _theme.EmbossColor = v, out _);
        RefreshVisuals(); Commit();
    }
    private void EmbossColor_Click(object sender, MouseButtonEventArgs e)
    {
        FocusAndSelectHex(EmbossColorBox);
    }
    private void EmbossHeight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _theme.EmbossHeight = EmbossHeightSlider.Value;
        EmbossHeightVal.Text = EmbossHeightSlider.Value.ToString("0.0") + "px";
        RefreshVisuals(); Commit();
    }
}
