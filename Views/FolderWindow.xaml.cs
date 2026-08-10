using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using DeskFolder.Models;
using DeskFolder.Services;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace DeskFolder.Views;

/// <summary>
/// 一个"安卓应用文件夹"浮动窗口。
/// 折叠时是一个浅灰半透明圆角块，内部以 3×3 网格预览前 9 个图标。
/// 悬停后窗口扩大到面板尺寸并播放展开动画（方框放大 + 内部图标变为桌面图标大小）；移出后收起。
/// 折叠图标可自由拖动摆放（不吸附桌面网格）。
/// </summary>
public partial class FolderWindow : Window
{
    private double CollapsedW = 150, CollapsedH = 150; // 计算后覆盖为 ≈2×2 桌面图标格
    private const double PanelMargin = 14;          // 与 XAML 中 Panel.Margin 一致
    private const double HeaderHeight = 34;
    private const double PanelPaddingH = 16 + PanelMargin * 2; // 左右留白
    private const double PanelPaddingV = 10 + PanelMargin * 2; // 上下留白

    // 折叠图标采用固定单元格像素尺寸（不再探测/吸附桌面网格）
    private const double IconCell = 75;   // 单个桌面图标格像素基准（折叠加倍）
    private const double IconSize = 32;   // 展开单元格内图标绘制尺寸
    private const double FoldMin = 60;    // 折叠图标自由缩放最小边（像素）
    private const double FoldMax = 600;   // 折叠图标自由缩放最大边（像素）
    private const double DefaultFoldPx = 150; // 折叠图标默认像素尺寸（未拖拽时）

    private readonly FolderConfig _config;
    private List<ShortcutItem> _items = new();
    private bool _expanded;
    private bool _animating;
    private double _collapsedLeft, _collapsedTop;

    private readonly DispatcherTimer _hoverTimer;    // 移入延迟展开（防抖）
    private readonly DispatcherTimer _collapseTimer; // 移出延迟收起

    // 防止右键菜单 / 文件夹设置窗口打开期间误触收起
    private bool _contextMenuOpen;
    private bool _settingsOpen;

    // 文字默认样式（主题未覆盖时还原为系统默认；在 OnLoaded 中从 XAML 实际取值）
    private FontFamily _defaultFont = SystemFonts.MessageFontFamily;
    private double _defaultFoldSize = 11;
    private double _defaultTitleSize = 13;
    private FontWeight _defaultFontWeight = FontWeights.Normal;

    // 主题渲染时动态插入的视觉元素（边框方框 / 图片背景），每次应用主题前先清空
    private readonly List<FrameworkElement> _themeVisuals = new();
    // 图片背景模式下 GIF 动图的逐帧计时器（切换主题 / 关闭窗口时必须停止，否则泄漏）
    private readonly List<DispatcherTimer> _gifTimers = new();
    // 图片轮播 / 随机播放的切换计时器（与 GIF 计时器分开管理）
    private readonly List<DispatcherTimer> _rotateTimers = new();
    private readonly Random _imgRnd = new();
    // 图片轮播状态槽：单图模式折叠/展开共用同一索引；多图模式各自独立
    private ImageSlot? _slotCollapsed;
    private ImageSlot? _slotExpanded;
    private int _singleIndex;

    // 折叠图标拖拽缩放模式：进入后右下角手柄可见，可自由拖动改变折叠图标大小
    private bool _resizeMode;

    // 拖动相关：折叠态用 OS 原生 this.DragMove()（在折叠图标上按下即拖动），平滑无卡顿；
    // 展开态按需求不提供拖动（保持 out_rel7 行为）。通过位移判定区分"拖动"与"轻点展开"。
    private bool _dragging;                 // 原生拖动（DragMove）进行中：期间禁用悬停逻辑，避免被打断
    private double _startLeft, _startTop;   // 拖动起点窗口坐标（用于判定是否发生位移）

    // 动画状态：窗口尺寸/位置只在"展开起点"和"收起终点"一次性改变（避免逐帧重排分层窗口导致的残影/抖动）；
    // 放大/缩小仅驱动内部 Panel 的 Width/Height（同一进度 → 宽高严格同步），窗口本身不动 → 无残影、位置稳定。
    private long _animStartTicks;
    private int _animMs;
    private bool _animExpand;
    private double _panelTargetW, _panelTargetH; // 展开后面板的最终尺寸（窗口 = 面板 + 2×边距）
    private double _panelFromW, _panelFromH, _panelToW, _panelToH; // 动画起止面板尺寸：展开=小→大，收起=大→小
    private const double WIN_PAD = 24;            // 窗口内边距（留出阴影空间，等同 XAML 中 Panel/CollapsedView 的 Margin）

    private static SettingsService S => App.Settings;

    /// <summary>当前窗口对应的文件夹配置（供 App 删除时定位）</summary>
    public FolderConfig Config => _config;

    /// <summary>有效列数：每文件夹覆盖优先，否则跟随全局设置</summary>
    private int EffectiveCols => _config.FolderColumns ?? S.Data.Columns;
    /// <summary>有效行数：每文件夹覆盖优先，否则跟随全局设置</summary>
    private int EffectiveRows => _config.FolderRows ?? S.Data.Rows;
    /// <summary>折叠图标有效像素尺寸：拖动产生的自由像素值优先，否则用默认像素尺寸。</summary>
    private (double W, double H) EffectiveFoldSize()
    {
        return (_config.FolderFoldW ?? DefaultFoldPx, _config.FolderFoldH ?? DefaultFoldPx);
    }

    public FolderWindow(FolderConfig config)
    {
        _config = config;
        InitializeComponent();

        FolderNameText.Text = config.Name;
        PanelTitle.Text = config.Name;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(S.Data.HoverDelayMs) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver) Collapse();
        };

        Loaded += OnLoaded;
        Closed += (_, _) => StopGifTimers(); // 窗口关闭时释放 GIF 计时器，避免泄漏
    }

    // ---------------- 初始化 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 捕获文字默认样式（主题未显式覆盖时还原为系统默认）
        _defaultFont = FolderNameText.FontFamily ?? SystemFonts.MessageFontFamily;
        _defaultFoldSize = FolderNameText.FontSize;
        _defaultTitleSize = PanelTitle.FontSize;
        _defaultFontWeight = FolderNameText.FontWeight;

        var (fw, fh) = EffectiveFoldSize();
        CollapsedW = fw;
        CollapsedH = fh;
        FolderChip.Width = CollapsedW;
        FolderChip.Height = CollapsedH;

        // 折叠图标（chip）的屏幕左上角位置；首次放在屏幕左侧（自由摆放，不吸附网格）
        var wa = SystemParameters.WorkArea;
        if (!double.IsNaN(_config.X) && !double.IsNaN(_config.Y))
        {
            _collapsedLeft = Math.Clamp(_config.X, wa.Left, wa.Right - CollapsedW);
            _collapsedTop = Math.Clamp(_config.Y, 0, wa.Bottom - CollapsedH);
        }
        else
        {
            _collapsedLeft = wa.Left + 40;
            _collapsedTop = wa.Top + 120 + S.Data.Folders.IndexOf(_config) * (CollapsedH + 24);
        }
        // 窗口左上角 = 图标左上角 - 边距；折叠态窗口尺寸 = 图标尺寸 + 2×边距（仅容纳阴影）。
        // 展开时只在起点一次性放大窗口，动画过程只缩放内部面板，故位置/透明度始终稳定。
        PlaceWindow();
        Width = CollapsedW + WIN_PAD * 2;
        Height = CollapsedH + WIN_PAD * 2;

        // 尺寸变化时同步刷新圆角裁剪（展开动画 / 折叠图标缩放都会触发）
        FolderChip.SizeChanged += (_, _) => ApplyBorderClip(FolderChip);
        Panel.SizeChanged += (_, _) => ApplyBorderClip(Panel);

        // 悬停展开 / 移开收起改由 WPF 原生 MouseEnter / MouseLeave 驱动（见 Window_MouseEnter / Window_MouseLeave）；
        // 折叠态拖动用 this.DragMove()（见 FolderChip_MouseLeftButtonDown）。均不挂任何原生钩子（out_rel7 行为）。

        ApplyTheme(); // 折叠图标 / 面板背景跟随当前主题
        LoadItems();
    }

    /// <summary>根据折叠图标位置一次性放置窗口（窗口左上角 = 图标 - 边距；位置固定，动画中不改动）</summary>
    private void PlaceWindow()
    {
        Left = _collapsedLeft - WIN_PAD;
        Top = _collapsedTop - WIN_PAD;
    }

    /// <summary>拖拽结束后，由当前窗口位置反推折叠图标位置</summary>
    private void SyncIconFromWindow()
    {
        _collapsedLeft = Left + WIN_PAD;
        _collapsedTop = Top + WIN_PAD;
    }

    /// <summary>后台线程解析快捷方式并提取图标，避免卡 UI</summary>
    private void LoadItems()
    {
        var links = _config.Shortcuts.ToList();
        Task.Run(() =>
        {
            var resolved = links
                .Select(l => ShortcutService.Resolve(l, loadIcon: true))
                .Where(i => i != null)
                .Cast<ShortcutItem>()
                .ToList();
            Dispatcher.Invoke(() =>
            {
                _items = resolved;
                BuildPreview();
                BuildGrid();
                if (_expanded && !_animating)
                {
                    // 内容数量变化后，重新计算并一次性应用到面板/窗口（非逐帧，无残影）
                    RecomputeTargets();
                    Panel.Width = _panelTargetW;
                    Panel.Height = _panelTargetH;
                    Width = AnimWindowW();
                    Height = AnimWindowH();
                }
            });
        });
    }

    /// <summary>折叠图标内的预览缩略图：按右键「显示排列」设定的行列排布，图标随框体大小实时等比缩放。
    /// 取宽/高中的「绑定维度」恰好撑满、另一维度居中留白，安全填充因子保证绝不溢出框体。</summary>
    private void BuildPreview()
    {
        PreviewGrid.Children.Clear();
        PreviewGrid.RowDefinitions.Clear();
        PreviewGrid.ColumnDefinitions.Clear();
        int rows = Math.Max(1, S.Data.PreviewRows);
        int cols = Math.Max(1, S.Data.PreviewCols);
        for (int i = 0; i < rows; i++)
            PreviewGrid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < cols; i++)
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition());

        double availW = Math.Max(1, CollapsedW - 24); // 左右各 12 边距
        double availH = Math.Max(1, CollapsedH - 42); // 上 12 + 底部名称条 30 预留
        double pitch = Math.Min(availW / cols, availH / rows); // 绑定维度恰好撑满，另一维度留白
        double mini = Math.Max(6, pitch - 4); // 每格图标尺寸（留 2px 边距，确保不溢出框体）
        foreach (var item in _items.Take(rows * cols))
        {
            var img = new System.Windows.Controls.Image
            {
                Width = mini, Height = mini,
                Source = item.Icon,
                Margin = new Thickness(2)
            };
            int idx = PreviewGrid.Children.Count;
            Grid.SetRow(img, idx / cols);
            Grid.SetColumn(img, idx % cols);
            PreviewGrid.Children.Add(img);
        }
    }

    /// <summary>展开面板中的图标网格</summary>
    private void BuildGrid()
    {
        IconGrid.Columns = EffectiveCols;
        IconGrid.Children.Clear();
        foreach (var item in _items)
            IconGrid.Children.Add(BuildCell(item));
    }

    /// <summary>展开单元格：图标为桌面图标大小，单元格间距固定</summary>
    private UIElement BuildCell(ShortcutItem item)
    {
        double icon = Math.Max(IconSize, 34);
        double cellW = IconCell, cellH = IconCell;

        var img = new System.Windows.Controls.Image
        {
            Width = icon, Height = icon,
            Source = item.Icon,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var textColor = CurrentTextColor();
        var text = new TextBlock
        {
            Text = item.Name,
            FontSize = Math.Max(10, icon * 0.34),
            Foreground = new SolidColorBrush(textColor),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = cellW - 10,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        if (IsImageMode())
            text.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, Opacity = 0.8, ShadowDepth = 0 };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var border = new Border
        {
            Width = cellW - 6,
            Height = cellH - 6,
            CornerRadius = new CornerRadius(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = item.Name,
            Child = stack
        };
        stack.Children.Add(img);
        stack.Children.Add(text);

        border.MouseEnter += (_, _) => border.Background =
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x78, 0xD7));
        border.MouseLeave += (_, _) => border.Background = System.Windows.Media.Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) =>
        {
            ShortcutService.Launch(item);
            Collapse();
        };
        return border;
    }

    // ---------------- 展开 / 收起 ----------------

    /// <summary>动画期间窗口应取的最小尺寸：必须同时装下「当前面板」与「折叠图标」两者（取较大值），
    /// 否则当折叠尺寸大于展开尺寸时，折叠图标超出窗口的部分会被裁切（收起动画末尾才突然弹出）。</summary>
    private double AnimWindowW() => Math.Max(_panelTargetW, CollapsedW) + WIN_PAD * 2;
    private double AnimWindowH() => Math.Max(_panelTargetH, CollapsedH) + WIN_PAD * 2;

    /// <summary>按当前图标数量计算展开后面板的最终尺寸，并夹取到工作区剩余空间（不足则滚动）。</summary>
    private void RecomputeTargets()
    {
        var d = S.Data;
        var wa = SystemParameters.WorkArea;
        double cellW = IconCell, cellH = IconCell;
        int cols = Math.Max(1, EffectiveCols);
        int total = _items.Count;
        int rows = Math.Max(EffectiveRows, (int)Math.Ceiling(total / (double)cols));
        rows = Math.Max(1, rows);

        double contentW = cols * cellW;
        double contentH = rows * cellH;
        double pw = contentW + PanelPaddingH;
        double ph = contentH + HeaderHeight + PanelPaddingV;

        // 锚点始终是折叠图标左上角（窗口不动），故只受右/下剩余空间约束；空间不足由 ScrollViewer 兜底
        double maxPW = (wa.Right - (_collapsedLeft - WIN_PAD)) - WIN_PAD * 2;
        double maxPH = (wa.Bottom - (_collapsedTop - WIN_PAD)) - WIN_PAD * 2;
        _panelTargetW = Math.Min(pw, Math.Max(CollapsedW, maxPW));
        _panelTargetH = Math.Min(ph, Math.Max(CollapsedH, maxPH));
    }

    private void Expand()
    {
        if (_expanded || _animating) return;
        _expanded = true;
        _animating = true;
        _collapseTimer.Stop();

        var d = S.Data;

        BuildGrid();
        RecomputeTargets();

        // 仅在展开起点一次性把窗口放大到「面板与折叠图标两者较大值」（这一次尺寸跳变会干净清除，无逐帧重排残影）；
        // 取较大值保证折叠尺寸 > 展开尺寸时，收起动画期间折叠图标也不会被窗口裁切；
        // 之后动画只缩放内部面板，图标左上角位置始终不变 → 文件夹从图标处向右下生长。
        Width = AnimWindowW();
        Height = AnimWindowH();

        CollapsedView.IsHitTestVisible = false;
        Panel.Visibility = Visibility.Visible;
        Panel.Width = CollapsedW;
        Panel.Height = CollapsedH;
        Panel.Opacity = 0;
        CollapsedView.Opacity = 1;
        AnimateTo(expand: true, d.AnimationMs);
    }

    private void Collapse()
    {
        if (!_expanded || _animating || _contextMenuOpen || _settingsOpen) return;
        _animating = true;
        _collapseTimer.Stop();
        CollapsedView.Visibility = Visibility.Visible; // 收起动画期间渐显
        // 收起动画比展开稍长，移出后视觉更从容（用户反馈收起太快）
        int ms = Math.Max((int)(S.Data.AnimationMs * 1.5), 300);
        AnimateTo(expand: false, ms);
    }

    /// <summary>
    /// 动画：用单一进度（每帧同时设置面板 Width/Height）驱动"放大 / 缩小"，
    /// 宽、高严格同步变化；窗口尺寸/位置全程不变，因此不会有逐帧重排造成的抖动、
    /// 边位移或透明层色差（残影）。
    /// </summary>
    private void AnimateTo(bool expand, int ms)
    {
        _animExpand = expand;
        _animMs = Math.Max(ms, 80);
        _animStartTicks = Stopwatch.GetTimestamp();

        // 起止面板尺寸：展开 = 从小(折叠尺寸)长大到目标；收起 = 从目标缩小回折叠尺寸。
        // 关键：收起必须「大→小」。若仍用 小→大 的公式，首帧 k≈0 会把已展开的面板瞬间跳成小尺寸，
        // 看上去就像展开态闪了一下再消失 —— 这正是之前"闪烁一瞬间"的根因。
        if (expand)
        {
            _panelFromW = CollapsedW;      _panelFromH = CollapsedH;
            _panelToW = _panelTargetW;     _panelToH = _panelTargetH;
            Panel.Opacity = 0;
            CollapsedView.Opacity = 1;
        }
        else
        {
            _panelFromW = _panelTargetW;   _panelFromH = _panelTargetH;
            _panelToW = CollapsedW;        _panelToH = CollapsedH;
            Panel.Opacity = 1;
            CollapsedView.Opacity = 0;
        }

        CompositionTarget.Rendering -= OnRenderFrame;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - _animStartTicks) / (double)Stopwatch.Frequency * 1000.0;
        double p = Math.Min(1.0, elapsedMs / _animMs);
        double k = EaseOutCubic(p); // 同一缓动 → 宽、高严格同步

        // 面板宽、高由同一进度驱动、同帧赋值；起止尺寸按展开/收起方向确定 → 宽高严格同步且方向正确
        Panel.Width = _panelFromW + (_panelToW - _panelFromW) * k;
        Panel.Height = _panelFromH + (_panelToH - _panelFromH) * k;

        if (_animExpand)
        {
            CollapsedView.Opacity = 1 - k;
            Panel.Opacity = k;
        }
        else
        {
            CollapsedView.Opacity = k;
            Panel.Opacity = 1 - k;
        }

        if (p >= 1.0)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _animating = false;

            if (_animExpand)
            {
                // 精确落到目标值，避免浮点残差
                Panel.Width = _panelTargetW;
                Panel.Height = _panelTargetH;
                Panel.Opacity = 1;
                CollapsedView.Visibility = Visibility.Collapsed;
                CollapsedView.Opacity = 1;
                if (!IsMouseOver) _collapseTimer.Start();
            }
            else
            {
                Panel.Visibility = Visibility.Collapsed;
                Panel.Opacity = 1;
                Panel.Width = CollapsedW;
                Panel.Height = CollapsedH;
                CollapsedView.Opacity = 1;
                CollapsedView.IsHitTestVisible = true;
                // 收起完成：窗口一次性缩回折叠尺寸（仅此一次跳变，无残影）
                Width = CollapsedW + WIN_PAD * 2;
                Height = CollapsedH + WIN_PAD * 2;
                _expanded = false;
                if (IsMouseOver)
                {
                    _hoverTimer.Interval = TimeSpan.FromMilliseconds(S.Data.HoverDelayMs);
                    _hoverTimer.Start();
                }
            }
        }
    }

    private static double EaseOutCubic(double p) => 1.0 - Math.Pow(1.0 - p, 3.0);

    // ---------------- 鼠标交互（悬停展开 / 移开收起 / 折叠态拖动） ----------------
    // 采用 WPF 原生鼠标事件（MouseEnter / MouseLeave）+ this.DragMove()：不挂任何原生钩子，
    // 因此 WPF 的 IsMouseOver / 鼠标事件完全正常，悬停展开与折叠态拖动都稳定（out_rel7 行为）。
    // 展开态按需求不提供拖动。

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_dragging || _resizeMode) return; // 拖动 / 缩放中不打断悬停判定
        if (!_expanded && !_animating)
        {
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(S.Data.HoverDelayMs);
            _hoverTimer.Start();
        }
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_expanded && !_animating && !_contextMenuOpen && !_settingsOpen)
            _collapseTimer.Start();
    }

    /// <summary>折叠图标按下：记录起点并启动 OS 原生平滑拖动（DragMove）。
    /// 松手后比较窗口位移：有位移→持久化新位置（并允许贴合屏幕最上边）；无位移→视为轻点展开。</summary>
    private void FolderChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;                 // 展开态不提供拖动（按需求）
        _startLeft = Left;
        _startTop = Top;
        _dragging = true;
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        this.DragMove();                        // 阻塞直到松手；OS 合成、平滑无卡顿
        _dragging = false;

        bool moved = Math.Abs(Left - _startLeft) > 2 || Math.Abs(Top - _startTop) > 2;
        if (moved)
        {
            SyncIconFromWindow();
            // 贴合屏幕最上边：OS 拖动通常把窗口顶到屏幕上沿(Left/Top≈0)，而图标默认在窗口内 WIN_PAD 处；
            // 此时强制把图标吸附到 y=0（窗口 Top 取 -WIN_PAD，透明窗口上沿多出的阴影被裁掉无妨）。
            if (Top <= 0 && _collapsedTop > 0) _collapsedTop = 0;
            PlaceWindow();
            _config.X = _collapsedLeft;
            _config.Y = _collapsedTop;
            S.Save();
        }
        else if (!_animating)
        {
            Expand();                           // 轻点（无位移）→ 展开
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_expanded && !_contextMenuOpen && !_settingsOpen) Collapse();
    }

    // 注：拖动已由 FolderChip_MouseLeftButtonDown 中的 this.DragMove() 处理，不再挂任何 WndProc 钩子。

    // 拖放 .lnk 到文件夹图标上 → 加入文件夹
    private void FolderChip_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderChip_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;
        bool added = false;
        foreach (var f in files)
        {
            if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(f)
                && !_config.Shortcuts.Contains(f))
            {
                _config.Shortcuts.Add(f);
                added = true;
            }
        }
        if (added)
        {
            S.Save();
            LoadItems();
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => Collapse();

    // ---------------- 右键菜单 / 文件夹设置 ----------------

    private void FolderMenu_Opened(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = true;
        _collapseTimer.Stop();
        // 仅在展开态显示「删除该文件夹」（折叠态不提供删除，避免误删）
        if (sender is ContextMenu menu)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && mi.Tag as string == "Delete")
                {
                    mi.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
                    break;
                }
            }
        }
    }

    private void FolderMenu_Closed(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = false;
        if (!IsMouseOver) _collapseTimer.Start();
    }

    /// <summary>打开「重命名文件夹」对话框，编辑文件夹名称。</summary>
    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new RenameWindow(_config, () =>
        {
            FolderNameText.Text = _config.Name;
            PanelTitle.Text = _config.Name;
        });
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>打开文件选择框，将选中的 .lnk 快捷方式加入当前文件夹。</summary>
    private void AddIconMenu_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图标到文件夹",
            Filter = "快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        bool added = false;
        foreach (var f in dlg.FileNames)
        {
            if (f.EndsWith(".lnk", System.StringComparison.OrdinalIgnoreCase)
                && File.Exists(f) && !_config.Shortcuts.Contains(f))
            {
                _config.Shortcuts.Add(f);
                added = true;
            }
        }
        if (added)
        {
            S.Save();
            LoadItems(); // 重新解析并刷新预览 / 面板
        }
    }

    /// <summary>打开「删除图标」对话框，勾选要从文件夹移除的图标。</summary>
    private void DeleteIconMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new ManageIconsWindow(_config);
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            if (win.DialogResult == true)
                LoadItems(); // 删除后刷新预览与面板
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>打开「展开图标显示排列」对话框，设置展开面板图标网格的行/列。</summary>
    private void ExpandArrangeMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new ExpandArrangeWindow(_config);
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>删除当前文件夹：仅移除 DeskFolder 中的分组，桌面上的真实 .lnk 不会被删除。</summary>
    private void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            $"确定要删除文件夹“{_config.Name}”吗？\n（仅移除该分组，桌面上的快捷方式不会被删除）",
            "删除文件夹",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        ((App)System.Windows.Application.Current).DeleteFolder(this);
    }

    /// <summary>进入折叠图标拖拽缩放模式：先收起到折叠态，再显示右下角手柄供自由拖动改变大小。</summary>
    private void SizeMenu_Click(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = false; // 绕过 Collapse 的右键菜单守卫
        _resizeMode = true;
        if (_expanded) Collapse(); // 收起后才能拖动折叠图标
        ResizeThumb.Visibility = Visibility.Visible;
        _hoverTimer.Stop();
    }

    /// <summary>打开「折叠图标显示排列」对话框，设置预览缩略图的行/列。</summary>
    private void ArrangeMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new FoldArrangeWindow();
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>拖拽右下角手柄：实时改变折叠图标像素尺寸（完全自由，无任何吸附），
    /// 内部图标随拖动逐帧等比缩放撑满框体。</summary>
    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(FoldMin, Math.Min(FolderChip.Width + e.HorizontalChange, FoldMax));
        double newH = Math.Max(FoldMin, Math.Min(FolderChip.Height + e.VerticalChange, FoldMax));
        FolderChip.Width = newW;
        FolderChip.Height = newH;
        CollapsedW = newW;
        CollapsedH = newH;
        Width = newW + WIN_PAD * 2;
        Height = newH + WIN_PAD * 2;
        BuildPreview(); // 内部图标随拖动实时变化
    }

    /// <summary>缩放结束：以自由像素尺寸持久化（不吸附整数格），并重排所有窗口。</summary>
    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ResizeThumb.Visibility = Visibility.Collapsed;
        _resizeMode = false;
        _config.FolderFoldW = CollapsedW;
        _config.FolderFoldH = CollapsedH;
        S.Save();
        S.NotifyChanged(); // 触发 RefreshLayout：按新自由尺寸干净重算
    }

    private void GlobalSettingsMenu_Click(object sender, RoutedEventArgs e) =>
        ((App)System.Windows.Application.Current).ShowSettings();

    /// <summary>打开「外观设置」对话框，单独设置「此文件夹」的外观主题（左键选主题即应用，右键编辑主题）。</summary>
    private void AppearanceMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new SettingsWindow(_config);
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>按当前主题设置折叠图标 / 展开面板的外观（填充 / 简约方框 / 图片背景），并自动选配文字颜色。</summary>
    private void ApplyTheme()
    {
        var theme = S.GetThemeForFolder(_config.FolderThemeId);
        ClearThemeVisuals();

        if (theme.Mode == ThemeMode.BorderOnly)
        {
            // 完全透明背景，仅画一个带圆角的边框方框
            FolderChip.Background = System.Windows.Media.Brushes.Transparent;
            Panel.Background = System.Windows.Media.Brushes.Transparent;
            ApplyFrame(FolderChip, theme);
            ApplyFrame(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Image)
        {
            SetupImageThemes(theme);
        }
        else // Fill：纯色背景 + 透明度
        {
            ThemeHelper.TryParseColor(theme.BackgroundColor, out var baseC);
            byte a = (byte)Math.Clamp(theme.BackgroundOpacity * 255, 0, 255);
            var bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, baseC.R, baseC.G, baseC.B));
            FolderChip.Background = bg;
            Panel.Background = bg;
        }

        double r = Math.Max(0, theme.CornerRadius);
        FolderChip.CornerRadius = new CornerRadius(r);
        Panel.CornerRadius = new CornerRadius(r);
        if (FolderNameBar != null)
            FolderNameBar.CornerRadius = new CornerRadius(0, 0, r, r);

        // 把 Border 的内容（图片背景 / 滚动条等）裁进圆角，避免展开态右上/右下被滚动条顶成方角
        ApplyBorderClip(FolderChip);
        ApplyBorderClip(Panel);

        // 文字（文件夹名称）样式：字体 / 大小 / 颜色 / 位置 / 隐藏，按主题设置应用到折叠名称条与展开标题
        ApplyTextSettings(theme);
    }

    /// <summary>
    /// 按主题"文字设置"渲染文件夹名称文字（折叠态名称条 + 展开态标题）：
    /// 字体（为空=系统默认）、大小（0=各状态默认）、颜色（为空=自动对比/白字）、
    /// 位置（底部/居中/顶部）、以及折叠/展开各自的隐藏开关。
    /// </summary>
    private void ApplyTextSettings(ThemeConfig theme)
    {
        // 字体
        var font = string.IsNullOrWhiteSpace(theme.TextFont)
            ? _defaultFont
            : new FontFamily(theme.TextFont);
        FolderNameText.FontFamily = font;
        PanelTitle.FontFamily = font;

        // 加粗
        var weight = theme.TextBold ? FontWeights.Bold : _defaultFontWeight;
        FolderNameText.FontWeight = weight;
        PanelTitle.FontWeight = weight;

        // 大小（0 = 跟随各状态默认）
        if (theme.TextSize > 0)
        {
            FolderNameText.FontSize = theme.TextSize;
            PanelTitle.FontSize = theme.TextSize;
        }
        else
        {
            FolderNameText.FontSize = _defaultFoldSize;
            PanelTitle.FontSize = _defaultTitleSize;
        }

        // 颜色（空 = 自动：填充/方框按背景亮度对比，图片模式白字 + 投影保证可读）
        bool explicitColor = ThemeHelper.TryParseColor(theme.TextColor, out var tc);
        Brush fg;
        DropShadowEffect? shadow = null;
        if (explicitColor)
        {
            fg = new SolidColorBrush(tc);
        }
        else
        {
            bool img = theme.Mode == ThemeMode.Image;
            Color auto = img ? Colors.White
                : (ThemeHelper.TryParseColor(
                       theme.Mode == ThemeMode.BorderOnly ? theme.BorderColor : theme.BackgroundColor,
                       out var bc) ? ThemeHelper.ContrastColor(bc) : Colors.White);
            fg = new SolidColorBrush(auto);
            if (img)
                shadow = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, Opacity = 0.85, ShadowDepth = 0 };
        }
        FolderNameText.Foreground = fg;
        PanelTitle.Foreground = fg;
        FolderNameText.Effect = shadow;
        PanelTitle.Effect = shadow;

        // 折叠态名称条：可见性 + 位置 + 预览留白
        FolderNameBar.Visibility = theme.HideTextCollapsed ? Visibility.Collapsed : Visibility.Visible;
        FolderNameBar.VerticalAlignment = theme.TextPosition switch
        {
            2 => VerticalAlignment.Top,
            1 => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom
        };
        PreviewGrid.Margin = theme.HideTextCollapsed ? new Thickness(12)
            : theme.TextPosition switch
            {
                2 => new Thickness(12, 30, 12, 12), // 名称置顶：上方留白
                1 => new Thickness(12),             // 名称居中：不预留（可能叠在预览上）
                _ => new Thickness(12, 12, 12, 30)  // 名称置底：下方留白（默认）
            };

        // 展开态标题：可见性 + 位置（置底 / 置顶）
        PanelTitle.Visibility = theme.HideTextExpanded ? Visibility.Collapsed : Visibility.Visible;
        PlaceTitle(theme.TextPosition);
        if (theme.HideTextExpanded)
            BottomTitleBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>根据文字位置把 PanelTitle 在顶部栏 / 底部栏之间迁移（0=底部，其余=顶部）。</summary>
    private void PlaceTitle(int position)
    {
        bool bottom = position == 0;
        if (bottom)
        {
            if (PanelTitle.Parent is Grid g && g != BottomTitleBar)
            {
                g.Children.Remove(PanelTitle);
                BottomTitleBar.Children.Add(PanelTitle);
            }
            PanelTitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            BottomTitleBar.Visibility = Visibility.Visible;
        }
        else
        {
            if (PanelTitle.Parent is Grid g && g != TopTitleBar)
            {
                g.Children.Remove(PanelTitle);
                TopTitleBar.Children.Add(PanelTitle);
            }
            PanelTitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            BottomTitleBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>将 Border 的内容裁剪为与 CornerRadius 一致的圆角矩形（解决图片 / 滚动条在圆角处溢出成方角）。</summary>
    private void ApplyBorderClip(Border b)
    {
        if (b == null) return;
        double r = b.CornerRadius.TopLeft;
        b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), r, r);
    }

    /// <summary>清空上一轮主题动态插入的边框 / 图片元素，并停止相关 GIF / 轮播计时器。</summary>
    private void ClearThemeVisuals()
    {
        ClearImageSlots();
        foreach (var v in _themeVisuals)
        {
            if (v.Parent is Grid g) g.Children.Remove(v);
            else if (v.Parent is Border b) b.Child = null;
        }
        _themeVisuals.Clear();
        StopGifTimers();
    }

    /// <summary>停止并清空所有 GIF 动图计时器。</summary>
    private void StopGifTimers()
    {
        foreach (var t in _gifTimers) t.Stop();
        _gifTimers.Clear();
    }

    /// <summary>在 Border 内部网格中叠加一个带圆角的边框方框（支持实线 / 虚线 / 点线 / 双线 / Win11 柔光）。</summary>
    private void ApplyFrame(Border target, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return;
        ThemeHelper.TryParseColor(theme.BorderColor, out var bc);
        var brush = new SolidColorBrush(bc);
        double t = Math.Max(0.5, theme.BorderThickness);
        double r = Math.Max(0, theme.CornerRadius);

        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;

        Rectangle Make(double inset, Brush stroke, double thick, int style)
        {
            var rect = new Rectangle
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(inset),
                RadiusX = Math.Max(0, r - inset),
                RadiusY = Math.Max(0, r - inset),
                Stroke = stroke,
                StrokeThickness = thick,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            if (style == 1) rect.StrokeDashArray = new DoubleCollection { 5, 4 };               // 虚线
            else if (style == 2)                                                              // 点线
            {
                rect.StrokeDashArray = new DoubleCollection { 1.2, 4 };
                rect.StrokeDashCap = PenLineCap.Round;
                rect.StrokeThickness = Math.Max(1.5, thick);
            }
            Grid.SetRowSpan(rect, rows);
            Grid.SetColumnSpan(rect, cols);
            return rect;
        }

        switch (theme.BorderStyle)
        {
            case 4: // Windows 11 风格：细描边 + 外侧柔光（贴合系统圆角浮层观感）
                {
                    var glow = Make(t / 2 + 3, new SolidColorBrush(Color.FromArgb(90, bc.R, bc.G, bc.B)), t + 5, 0);
                    var line = Make(t / 2, brush, t, 0);
                    grid.Children.Insert(0, glow); _themeVisuals.Add(glow);
                    grid.Children.Insert(0, line); _themeVisuals.Add(line);
                    break;
                }
            case 3: // 双线
                {
                    var outer = Make(t / 2, brush, t, 0);
                    var inner = Make(t / 2 + t + 3, brush, t, 0);
                    grid.Children.Insert(0, outer); _themeVisuals.Add(outer);
                    grid.Children.Insert(0, inner); _themeVisuals.Add(inner);
                    break;
                }
            default: // 0 实线 / 1 虚线 / 2 点线
                {
                    var line = Make(t / 2, brush, t, theme.BorderStyle);
                    grid.Children.Insert(0, line); _themeVisuals.Add(line);
                    break;
                }
        }
    }

    /// <summary>图片轮播状态槽：绑定一个 Border（折叠图标或展开面板）+ 一组图片 + 当前索引，
    /// 负责按播放方式（轮流/随机）与间隔切换图片，并保留 GIF 逐帧动画与各自裁剪。</summary>
    private class ImageSlot
    {
        public Border Target = null!;
        public ImagePlaylist Playlist = null!;
        public ThemeConfig Theme = null!;
        public bool UseExpandedCrop;
        public System.Windows.Controls.Image Img = null!;
        public int Index;
        public DispatcherTimer? GifTimer;
    }

    /// <summary>按图片主题（单图 / 多图）为折叠图标与展开面板建立图片轮播槽。
    /// 单图模式：两者共用同一组图且同步索引（始终显示同一张）；
    /// 多图模式：折叠/展开各自独立一组，播放方式与间隔分开设置。</summary>
    private void SetupImageThemes(ThemeConfig theme)
    {
        ClearImageSlots();
        if (theme.ImageLayout == ImageLayoutMode.Single)
        {
            _singleIndex = 0;
            _slotCollapsed = CreateSlot(FolderChip, theme.Single, false, theme);
            _slotExpanded = CreateSlot(Panel, theme.Single, true, theme);
            ReloadSlot(_slotCollapsed, _singleIndex);
            ReloadSlot(_slotExpanded, _singleIndex);
            if (theme.Single.Play != ImagePlayMode.Off && theme.Single.Paths.Count > 1)
                StartRotate(true, null);
        }
        else
        {
            _slotCollapsed = CreateSlot(FolderChip, theme.Collapsed, false, theme);
            _slotExpanded = CreateSlot(Panel, theme.Expanded, true, theme);
            ReloadSlot(_slotCollapsed, 0);
            ReloadSlot(_slotExpanded, 0);
            if (theme.Collapsed.Play != ImagePlayMode.Off && theme.Collapsed.Paths.Count > 1)
                StartRotate(false, _slotCollapsed);
            if (theme.Expanded.Play != ImagePlayMode.Off && theme.Expanded.Paths.Count > 1)
                StartRotate(false, _slotExpanded);
        }
    }

    /// <summary>在 Border 内部网格中插入一个铺满的图片元素，返回状态槽。</summary>
    private ImageSlot? CreateSlot(Border target, ImagePlaylist playlist, bool useExpandedCrop, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return null;
        var img = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = ThemeHelper.Clamp(theme.BackgroundOpacity, 0, 1),
            IsHitTestVisible = false
        };
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        Grid.SetRowSpan(img, rows);
        Grid.SetColumnSpan(img, cols);
        grid.Children.Insert(0, img);
        _themeVisuals.Add(img);
        target.Background = System.Windows.Media.Brushes.Transparent;
        return new ImageSlot { Target = target, Playlist = playlist, UseExpandedCrop = useExpandedCrop, Theme = theme, Img = img };
    }

    /// <summary>按裁剪区域（折叠态用 ImageCrop*、展开态用 ImageCropExpanded*）对单帧取景；Stretch=Fill 保证选区精确铺满。</summary>
    private BitmapSource CropFrame(BitmapSource src, bool expanded, ThemeConfig theme)
    {
        bool hasCrop = expanded ? theme.HasImageCropExpanded : theme.HasImageCrop;
        if (!hasCrop) return src;
        double nx = (expanded ? theme.ImageCropExpandedX : theme.ImageCropX)!.Value;
        double ny = (expanded ? theme.ImageCropExpandedY : theme.ImageCropY)!.Value;
        double nw = (expanded ? theme.ImageCropExpandedW : theme.ImageCropW)!.Value;
        double nh = (expanded ? theme.ImageCropExpandedH : theme.ImageCropH)!.Value;
        int x = (int)Math.Round(nx * src.PixelWidth);
        int y = (int)Math.Round(ny * src.PixelHeight);
        int w = (int)Math.Round(nw * src.PixelWidth);
        int h = (int)Math.Round(nh * src.PixelHeight);
        x = Math.Max(0, Math.Min(x, src.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, src.PixelHeight - 1));
        w = Math.Max(1, Math.Min(w, src.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, src.PixelHeight - y));
        return new CroppedBitmap(src, new Int32Rect(x, y, w, h));
    }

    /// <summary>重新加载某槽到指定索引的图片（含 GIF 逐帧动画接管）。裁剪随该状态（折叠/展开）各自生效。</summary>
    private void ReloadSlot(ImageSlot? slot, int index)
    {
        if (slot == null) return;
        if (slot.GifTimer != null) { _gifTimers.Remove(slot.GifTimer); slot.GifTimer.Stop(); slot.GifTimer = null; }
        var playlist = slot.Playlist;
        if (playlist.Paths.Count == 0)
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)); // 兜底淡底色
            return;
        }
        index = ((index % playlist.Paths.Count) + playlist.Paths.Count) % playlist.Paths.Count;
        string path = playlist.Paths[index];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            return;
        }
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = decoder.Frames;
            slot.Img.Source = CropFrame(frames[0], slot.UseExpandedCrop, slot.Theme);
            if (frames.Count > 1)
            {
                // 动画 GIF：按每帧自带延迟逐帧切换（单位 1/100 秒）
                var delays = new List<TimeSpan>(frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    int d = 10; // 默认 100ms
                    try
                    {
                        if (frames[i].Metadata is BitmapMetadata md && md.ContainsQuery("/grctlext/Delay"))
                            d = (int)(ulong)md.GetQuery("/grctlext/Delay");
                    }
                    catch { }
                    if (d <= 0) d = 10;
                    delays.Add(TimeSpan.FromMilliseconds(d * 10));
                }
                int idx = 0;
                var gtimer = new DispatcherTimer();
                gtimer.Tick += (_, _) =>
                {
                    idx = (idx + 1) % frames.Count;
                    slot.Img.Source = CropFrame(frames[idx], slot.UseExpandedCrop, slot.Theme);
                    gtimer.Interval = delays[idx];
                };
                gtimer.Interval = delays[0];
                gtimer.Start();
                slot.GifTimer = gtimer;
                _gifTimers.Add(gtimer);
            }
            slot.Target.Background = System.Windows.Media.Brushes.Transparent;
        }
        catch
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
        }
    }

    /// <summary>启动轮播/随机切换计时器。shared=true 表示单图模式（折叠/展开共用索引，两者同步切换）；
    /// shared=false 时针对单个槽（多图模式的折叠或展开）。</summary>
    private void StartRotate(bool shared, ImageSlot? slot)
    {
        var playlist = shared ? (_slotCollapsed?.Playlist ?? _slotExpanded?.Playlist) : slot?.Playlist;
        if (playlist == null || playlist.Paths.Count <= 1) return;
        int minutes = Math.Clamp(playlist.IntervalMinutes, 1, 120);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        timer.Tick += (_, _) =>
        {
            if (shared)
            {
                _singleIndex = NextIndex(_singleIndex, playlist);
                ReloadSlot(_slotCollapsed, _singleIndex);
                ReloadSlot(_slotExpanded, _singleIndex);
            }
            else if (slot != null)
            {
                slot.Index = NextIndex(slot.Index, playlist);
                ReloadSlot(slot, slot.Index);
            }
        };
        timer.Start();
        _rotateTimers.Add(timer);
    }

    /// <summary>按播放方式计算下一张索引：轮流=顺序循环；随机=换一张不同的；其余=保持。</summary>
    private int NextIndex(int cur, ImagePlaylist playlist)
    {
        if (playlist.Paths.Count == 0) return 0;
        if (playlist.Play == ImagePlayMode.Random)
        {
            if (playlist.Paths.Count == 1) return 0;
            int n;
            do { n = _imgRnd.Next(playlist.Paths.Count); } while (n == cur);
            return n;
        }
        // 轮流（顺序循环）
        return (cur + 1) % playlist.Paths.Count;
    }

    /// <summary>停止并清空图片轮播槽（含轮播计时器），不触碰 GIF 计时器（由 ClearThemeVisuals 统一处理）。</summary>
    private void ClearImageSlots()
    {
        foreach (var t in _rotateTimers) t.Stop();
        _rotateTimers.Clear();
        _slotCollapsed = null;
        _slotExpanded = null;
    }

    /// <summary>当前是否为图片背景模式。</summary>
    private bool IsImageMode() => S.GetThemeForFolder(_config.FolderThemeId).Mode == ThemeMode.Image;

    /// <summary>根据当前主题决定图标文字颜色（图片模式白字，其余按背景亮度对比）。</summary>
    private Color CurrentTextColor()
    {
        var t = S.GetThemeForFolder(_config.FolderThemeId);
        if (t.Mode == ThemeMode.Image) return Colors.White;
        ThemeHelper.TryParseColor(t.BackgroundColor, out var c);
        return ThemeHelper.ContrastColor(c);
    }

    /// <summary>设置变更后重排（折叠尺寸 / 展开行列变化均即时生效）</summary>
    public void RefreshLayout()
    {
        // 折叠尺寸可能变化 → 同步图标块与预览
        var (fw, fh) = EffectiveFoldSize();
        CollapsedW = fw;
        CollapsedH = fh;
        FolderChip.Width = CollapsedW;
        FolderChip.Height = CollapsedH;

        ApplyTheme(); // 主题变更即时反映到折叠图标与面板

        BuildPreview();
        BuildGrid();
        if (_expanded && !_animating)
        {
            RecomputeTargets();
            Panel.Width = _panelTargetW;
            Panel.Height = _panelTargetH;
            Width = AnimWindowW();
            Height = AnimWindowH();
        }
        else
        {
            // 折叠态：尺寸变化后重新放置窗口并持久化新位置（自由摆放，不吸附网格）
            PlaceWindow();
            Width = CollapsedW + WIN_PAD * 2;
            Height = CollapsedH + WIN_PAD * 2;
            _config.X = _collapsedLeft;
            _config.Y = _collapsedTop;
            S.Save();
        }
    }
}
