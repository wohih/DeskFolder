using System.IO;
using System.Linq;
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
    private const double IconSizeNoName = 46; // 隐藏软件名称模式下展开单元格内图标绘制尺寸（放大防止间隙过大）
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
    // 插件渲染时动态插入的视觉元素，每次 ApplyPlugins 前清空 + 停止插件计时器
    private readonly List<FrameworkElement> _pluginVisuals = new();
    // 插件专属计时器：模拟时钟秒针、日历刷新等（与 GIF/轮播分开管理）
    private readonly List<DispatcherTimer> _pluginTimers = new();
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

    // 拖动相关：折叠态用 CaptureMouse + MouseMove 手动拖动（窗口挂到桌面后 DragMove 不再可靠）；
    // 展开态用鼠标中键按住面板任意位置拖动（左键预留给按钮/交互，中键不激活窗口，避免置顶 bug）。
    private bool _dragging;                 // 拖动进行中：期间禁用悬停逻辑，避免被打断
    private bool _mouseDown;                // 折叠图标鼠标左键按住中（手动拖动标志）
    private WpfPoint _dragScreenStart;      // 拖动起点（物理屏幕坐标）
    private double _winLeftStart, _winTopStart; // 拖动起点窗口左上角（逻辑坐标，用于判定位移）
    private double _dpiScaleX = 1.0, _dpiScaleY = 1.0; // DPI 缩放（物理/逻辑）：PointToScreen 返回物理坐标，需换算回逻辑坐标才能赋给 Left/Top

    // 动画状态：窗口尺寸/位置只在"展开起点"和"收起终点"一次性改变（避免逐帧重排分层窗口导致的残影/抖动）；
    // 放大/缩小仅驱动内部 Panel 的 Width/Height（同一进度 → 宽高严格同步），窗口本身不动 → 无残影、位置稳定。
    private long _animStartTicks;
    private int _animMs;
    private bool _animExpand;
    private double _panelTargetW, _panelTargetH; // 展开后面板的最终尺寸（窗口 = 面板 + 2×边距）
    private double _panelFromW, _panelFromH, _panelToW, _panelToH; // 动画起止面板尺寸：展开=小→大，收起=大→小
    private const double WIN_PAD = 24;            // 窗口内边距（留出阴影空间，等同 XAML 中 Panel/CollapsedView 的 Margin）

    // 网格拖拽相关字段
    private bool _gridItemDragging;          // 网格项正在拖动
    private FrameworkElement? _dragSource;   // 拖动源元素
    private string? _dragItemId;              // 拖动物体标识（插件 GridId 或图标路径）
    private string? _dragItemType;           // "plugin" 或 "shortcut"
    private WpfPoint _dragStartPoint;         // 拖动起点
    private int _dragOverRow, _dragOverCol;   // 当前拖拽目标位置
    private Border? _dropIndicator;          // 放置指示线
    private double _dragItemWidth, _dragItemHeight; // 拖动物品尺寸

    // 音乐播放器相关
    private MusicService? _musicService;
    private FolderPlugin? _musicPlayerPlugin;
    private Border? _musicPlayerCollapsed;
    private Border? _musicPlayerExpanded;
    private bool _musicPinned; // 音乐播放器是否固定展开

    // 静态图标Geometry缓存（key: 类型+"_"+size），避免每次ApplyPlugins都创建新的Geometry对象造成GC卡顿
    private static readonly Dictionary<string, Geometry> _geomCache = new();

    // 折叠态UI元素引用
    private TextBlock? _musicTitleMarquee;
    private TextBlock? _musicArtistText;
    private Border? _musicAlbumArt;
    private UIElement? _musicAlbumArtContent; // 折叠态封面占位内容（K logo 渐变），有真实封面时隐藏
    private Button? _musicPlayPauseBtn;
    private Button? _musicPrevBtn;
    private Button? _musicNextBtn;

    // 展开态UI元素引用
    private Border? _musicExpandedAlbumArt;
    private UIElement? _musicExpandedAlbumArtContent; // 展开态封面占位内容
    private TextBlock? _musicExpandedTitle;
    private TextBlock? _musicExpandedArtist;
    private Button? _musicExpandedPlayPauseBtn;
    private Button? _musicExpandedPrevBtn;
    private Button? _musicExpandedNextBtn;
    private Button? _musicPinBtn;
    private ScrollViewer? _musicLyricsScroll;
    private StackPanel? _musicLyricsPanel;
    private readonly List<TextBlock> _musicLyricLineElements = new();

    // 歌词平滑滚动动画状态：CompositionTarget.Rendering 帧驱动（与展开/收起动画共用 OnRenderFrame 订阅），
    // 动画结束即退订；动画中来新目标时从当前实际偏移重定向。
    private bool _lyricsAnimActive;
    private double _lyricsAnimFrom;
    private double _lyricsAnimTo;
    private long _lyricsAnimStartTicks;
    private const int LyricsAnimMs = 400; // 滚动时长（需求区间 350-450ms）
    private bool _lyricsSnapNext;         // RebuildLyricsPanel 后置位：下次滚动直接落位
    private int _lastLyricIndex = -1;     // 上一个当前行索引（首次 -1→0 直接落位）

    // 图标面板滚轮平滑滚动动画状态：与歌词/放大动画共用 OnRenderFrame 调度器（_iconScrollActive 门控，仿 _lyricsAnimActive 模式）。
    // 横向模式下把竖向滚轮映射为横向偏移；纵向模式接管默认竖向滚动；两者均做 EaseOutCubic 缓动，避免逐格硬跳。
    private bool _iconScrollActive;
    private double _iconScrollFrom;
    private double _iconScrollTo;
    private long _iconScrollStartTicks;
    private bool _iconScrollHorizontal;       // true=横向轴 / false=纵向轴
    private const int IconScrollAnimMs = 320; // 滚轮平滑滚动时长
    private bool _scrollHorizontal;           // 当前展开网格滚动方向（供边缘淡出遮罩判定轴向）

    // 隐藏软件名称模式的悬停名称标签：MouseEnter 后延迟 ~700ms 在图标上方浮出（窗口级 OverlayLayer，
    // 在格子之外，不受 Dock 缩放影响）；MouseLeave/点击/拖拽开始立即取消并隐藏；同一时间只有一个。
    private readonly DispatcherTimer _hoverNameTimer;  // 悬停延迟计时器
    private Border? _hoverNameLabel;                   // 当前显示的名称浮层（OverlayLayer 内）
    private Border? _hoverNameCell;                    // 悬停延迟中 / 显示中的目标格子
    private string _hoverNameText = "";                // 待显示的名称文本
    private const int HoverNameDelayMs = 700;          // 悬停多久后浮出名称

    // Dock 放大（Magnification）：展开态鼠标附近快捷方式格子随距离平滑放大（macOS Dock 风格）。
    // 帧驱动挂进 OnRenderFrame 调度器（_magnifyActive 门控，仿 _lyricsAnimActive 模式）；
    // 缩放用 RenderTransform（ScaleTransform，中心原点），不引起重新布局；插件格子不参与。
    private const double MagnifyMaxExtra = 0.4;        // 中心格子最大额外放大比例（32→~45）
    private const double MagnifyRadiusFactor = 1.2;    // 影响半径 = 1.2 × IconCell
    private const double MagnifyLerp = 0.25;           // 每帧当前缩放向目标逼近的插值系数
    private const double MagnifyDeadZone = 0.01;       // 死区：与目标差值小于该值直接置目标，防永动
    private bool _magnifyActive;                       // 放大动画是否挂在帧调度器上
    private bool _magnifyMouseInside;                  // 鼠标是否在 IconGrid 内（离开则全部回 1.0）
    private readonly List<MagnifyCell> _magnifyCells = new(); // 参与放大的快捷方式格子（随 BuildGrid 重建）

    /// <summary>参与 Dock 放大的快捷方式格子：单元格 + 其 RenderTransform 缩放实例 + 当前目标缩放。</summary>
    private sealed class MagnifyCell
    {
        public FrameworkElement Cell = null!;
        public ScaleTransform Scale = null!;
        public double Target = 1.0;
    }

    /// <summary>展开动画时按「行优先（上→下、左→右）」有序排列的快捷方式格子，用于错峰淡入。</summary>
    private readonly List<Border> _orderedCells = new();

    private static SettingsService S => App.Settings;

    /// <summary>当前窗口对应的文件夹配置（供 App 删除时定位）</summary>
    public FolderConfig Config => _config;

    /// <summary>当前文件夹是否为「贴边文件夹」主题（折叠态贴屏白色方框、展开态同图片主题）。</summary>
    private bool IsEdgeFolder() => S.GetThemeForFolder(_config.FolderThemeId).Mode == ThemeMode.Edge;

    /// <summary>主屏逻辑宽度 / 高度（用于贴边定位；SystemParameters 单位为逻辑 DIP，与 Window.Left/Top 一致）。</summary>
    private static double ScreenW => SystemParameters.PrimaryScreenWidth;
    private static double ScreenH => SystemParameters.PrimaryScreenHeight;

    /// <summary>有效列数：每文件夹覆盖优先，否则跟随全局设置</summary>
    private int EffectiveCols => _config.FolderColumns ?? S.Data.Columns;
    /// <summary>有效行数：每文件夹覆盖优先，否则跟随全局设置</summary>
    private int EffectiveRows => _config.FolderRows ?? S.Data.Rows;
    /// <summary>有效滚动方向：每文件夹覆盖优先，否则跟随全局设置（0=纵向滚动 / 1=横向滚动）</summary>
    private int EffectiveScroll => _config.FolderExpandScroll ?? S.Data.ExpandScroll;
    /// <summary>折叠图标有效像素尺寸：贴边文件夹用其白色方框尺寸；否则拖动产生的自由像素值优先，再否则默认像素尺寸。</summary>
    private (double W, double H) EffectiveFoldSize()
    {
        if (IsEdgeFolder())
            return (_config.EdgeBoxWidth, _config.EdgeBoxHeight);
        return (_config.FolderFoldW ?? DefaultFoldPx, _config.FolderFoldH ?? DefaultFoldPx);
    }

    public FolderWindow(FolderConfig config)
    {
        _config = config;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        // 启用硬件加速以提升动画流畅度
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        UseLayoutRounding = false;

        FolderNameText.Text = config.Name;
        PanelTitle.Text = config.Name;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(S.Data.HoverDelayMs) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Expand(); };
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver && !_gridItemDragging) Collapse();
        };

        // 隐藏软件名称模式：悬停延迟后浮出名称标签
        _hoverNameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverNameDelayMs) };
        _hoverNameTimer.Tick += (_, _) => { _hoverNameTimer.Stop(); ShowHoverNameLabel(); };

        // Dock 放大：在展开态 IconGrid 上跟踪鼠标（事件由格子向上冒泡到 IconGrid）
        IconGrid.MouseMove += IconGrid_MagnifyMouseMove;
        IconGrid.MouseLeave += IconGrid_MagnifyMouseLeave;

        // 横向滚动模式：把鼠标滚轮映射为横向滚动（无横向滚轮的普通鼠标也能滚动），纵向模式放行默认竖向滚动
        if (IconScroller != null)
        {
            IconScroller.PreviewMouseWheel += IconScroller_PreviewMouseWheel;
            // 可滚动范围随展开动画/面板尺寸/内容变化而改变，实时刷新边缘淡入淡出遮罩
            IconScroller.ScrollChanged += (_, _) => UpdateScrollFade();
        }

        Loaded += OnLoaded;
        Closed += (_, _) => { StopGifTimers(); StopAllVideos(); CleanupMusicService(); }; // 关闭时释放 GIF/视频/轮播计时器并退订共享音乐服务，避免泄漏
    }

    // ---------------- 原生窗口样式：隐藏任务切换器 / 桌面挂件化（兼容 Wallpaper Engine） ----------------

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000; // 点击不激活窗口，避免 Z 序被提升到普通窗口之上

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>定位桌面壁纸所在的 WorkerW 窗口：壁纸（含 Wallpaper Engine）渲染在它之上、普通应用窗口之下。</summary>
    private static IntPtr FindWorkerW()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        IntPtr workerw = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                workerw = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return workerw;
    }

    /// <summary>窗口句柄就绪后：1) 加 WS_EX_TOOLWINDOW 使其不出现在 Alt+Tab 与任务栏；
    /// 2) 挂到桌面 WorkerW，成为"桌面挂件"——位于壁纸之上、普通窗口之下（兼容 Wallpaper Engine）。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | (int)WS_EX_TOOLWINDOW | (int)WS_EX_NOACTIVATE);

        IntPtr workerw = FindWorkerW();
        if (workerw != IntPtr.Zero && workerw != hwnd)
            SetParent(hwnd, workerw);

        // 缓存 DPI 缩放（物理像素 / 逻辑 DIP）。150% 缩放 => 1.5。
        // 拖动时用它把 PointToScreen 得到的物理位移换算回逻辑坐标，否则窗口会以 1.5 倍速跟手。
        var ps = PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget != null)
        {
            _dpiScaleX = ps.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = ps.CompositionTarget.TransformToDevice.M22;
        }
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
        else if (IsEdgeFolder())
        {
            // 贴边文件夹默认贴边：顶→水平居中且贴顶；左/右→贴边、纵向默认 160
            int ea = _config.EdgeAnchor;
            if (ea == 1) { _collapsedLeft = ScreenW / 2 - CollapsedW / 2; _collapsedTop = 0; }
            else { _collapsedLeft = 0; _collapsedTop = 160; }
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

        // 初始化插件宿主可见性（折叠态显示折叠插件，隐藏展开插件宿主）
        PluginHostCollapsed.Visibility = Visibility.Visible;
        PluginHostCollapsed.Opacity = 1;
        PluginHostExpanded.Visibility = Visibility.Collapsed;

        ApplyTheme(); // 折叠图标 / 面板背景跟随当前主题
        LoadItems();
    }

    /// <summary>根据折叠图标位置一次性放置窗口（窗口左上角 = 图标 - 边距；位置固定，动画中不改动）。
    /// 贴边文件夹改按 EdgeAnchor 计算位置。</summary>
    private void PlaceWindow()
    {
        if (IsEdgeFolder()) { ApplyEdgePosition(_expanded); return; }
        Left = _collapsedLeft - WIN_PAD;
        Top = _collapsedTop - WIN_PAD;
    }

    /// <summary>贴边文件夹：按 EdgeAnchor 计算窗口位置。折叠态贴屏（距边 0），展开态距屏 EdgeDistance。</summary>
    private void ApplyEdgePosition(bool expanded)
    {
        int a = _config.EdgeAnchor;
        double perp = expanded ? _config.EdgeDistance : 0; // 折叠贴边、展开留距
        double sizeW = expanded ? _panelTargetW : CollapsedW;
        double sizeH = expanded ? _panelTargetH : CollapsedH;
        // 沿边坐标：左/右沿竖直(_collapsedTop)，顶沿水平(_collapsedLeft)
        double along = (a == 2 || a == 3) ? _collapsedTop : _collapsedLeft;
        switch (a)
        {
            case 1: // 顶：横向=along，纵向贴边
                Left = along - WIN_PAD; Top = perp - WIN_PAD; break;
            case 2: // 左：纵向=along，横向贴边
                Left = perp - WIN_PAD; Top = along - WIN_PAD; break;
            case 3: // 右：纵向=along，横向贴边+尺寸
                Left = ScreenW - perp - WIN_PAD - sizeW; Top = along - WIN_PAD; break;
            default:
                Left = _collapsedLeft - WIN_PAD; Top = _collapsedTop - WIN_PAD; break;
        }
    }

    /// <summary>贴边文件夹拖拽：把沿边坐标限制在屏幕范围内（不越界）。</summary>
    private void ClampEdgeAlong()
    {
        int a = _config.EdgeAnchor;
        if (a == 1) // 顶：横向限制
            Left = Math.Max(-WIN_PAD, Math.Min(Left, ScreenW - CollapsedW - WIN_PAD));
        else // 左/右：纵向限制
            Top = Math.Max(-WIN_PAD, Math.Min(Top, ScreenH - CollapsedH - WIN_PAD));
    }

    /// <summary>贴边文件夹折叠态：在 FolderChip 上绘制贴屏白色透明方框——贴屏两角无圆角、另两角小圆角；
    /// 隐藏内部预览图标与名称条，方框尺寸 / 透明度 / 圆角取自 FolderConfig 贴边设置。</summary>
    private void ApplyEdgeCollapsedVisual()
    {
        if (!IsEdgeFolder()) return;
        int anchor = _config.EdgeAnchor;
        double op = ThemeHelper.Clamp(_config.EdgeBoxOpacity, 0, 1);
        double w = Math.Max(20, _config.EdgeBoxWidth);
        double h = Math.Max(20, _config.EdgeBoxHeight);
        double r = Math.Max(0, _config.EdgeBoxCorner);

        CollapsedW = w;
        CollapsedH = h;
        FolderChip.Width = w;
        FolderChip.Height = h;
        FolderChip.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(op * 255), 255, 255, 255));
        FolderChip.BorderThickness = new Thickness(0);
        FolderChip.Clip = null; // 仅纯色方框、无内部溢出内容，无需圆角裁剪

        // 贴屏两角无圆角，自由两角取小圆角（CornerRadius 顺序：左上、右上、右下、左下）
        FolderChip.CornerRadius = anchor switch
        {
            1 => new CornerRadius(0, 0, r, r),   // 顶：贴上方，下两角圆
            3 => new CornerRadius(r, 0, 0, r),   // 右：贴右方，左两角圆
            _ => new CornerRadius(0, r, r, 0),   // 左（默认）：贴左方，右两角圆
        };

        // 仅显示白色方框：隐藏内部图标预览与名称条
        PreviewGrid.Visibility = Visibility.Collapsed;
        FolderNameBar.Visibility = Visibility.Collapsed;

        // 折叠态不显示图片槽（仅白色方框）；隐藏其视觉并置空，避免遮挡
        if (_slotCollapsed != null)
        {
            StopVideo(_slotCollapsed);
            if (_slotCollapsed.Host != null) _slotCollapsed.Host.Visibility = Visibility.Collapsed;
        }
        _slotCollapsed = null;
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

        // 折叠态隐藏图标（图片主题常用）：仅显示背景/图片，不显示内部应用缩略图；展开态不受影响
        var theme = S.GetThemeForFolder(_config.FolderThemeId);
        PreviewGrid.Visibility = theme.HideIconCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (theme.HideIconCollapsed) return;

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

    /// <summary>展开面板中的图标+插件混合网格</summary>
    /// <summary>展开面板中的图标+插件混合网格。视口严格为「设定行列」；图标超出行列时按滚动方向溢出：
    /// 纵向滚动=固定列数、行向下增长（垂直滚动条）；横向滚动=固定行数、列向右增长（水平滚动条）。</summary>
    private void BuildGrid()
    {
        // 重建网格：清空 Dock 放大登记（旧格子的 ScaleTransform 一并失效）并隐藏可能残留的名称浮层
        _magnifyCells.Clear();
        HideHoverNameLabel();
        _iconScrollActive = false; // 网格重建期间取消在途滚轮平滑滚动，避免与新内容偏移打架

        int viewCols = Math.Max(1, EffectiveCols);   // 视口列数（排列设定）
        int viewRows = Math.Max(1, EffectiveRows);   // 视口行数（排列设定）
        bool horizontal = EffectiveScroll == 1;      // true=横向滚动, false=纵向滚动
        _scrollHorizontal = horizontal;

        // 收集所有插件（展开态显示的）
        var expandedPlugins = _config.Plugins?
            .Where(p => p.ShowOnExpanded && p.Type != FolderPluginType.None)
            .ToList() ?? new List<FolderPlugin>();

        // 插件所需的最大行列（含跨度），用于保证插件不被裁切
        int pluginMaxRow = 0, pluginMaxCol = 0;
        foreach (var p in expandedPlugins)
        {
            int er = (p.GridRow >= 0 ? p.GridRow : 0) + p.GridRowSpan;
            int ec = (p.GridColumn >= 0 ? p.GridColumn : 0) + p.GridColSpan;
            pluginMaxRow = Math.Max(pluginMaxRow, er);
            pluginMaxCol = Math.Max(pluginMaxCol, ec);
        }

        // 计算网格总尺寸：非滚动维度至少容纳插件；滚动维度随图标数量增长（溢出由滚动条显示）
        int totalCols, totalRows;
        if (horizontal)
        {
            totalRows = Math.Max(viewRows, pluginMaxRow);                 // 行固定视口，插件越界则扩展
            int iconCols = (int)Math.Ceiling(_items.Count / (double)Math.Max(1, totalRows));
            totalCols = Math.Max(viewCols, Math.Max(iconCols, pluginMaxCol));
        }
        else
        {
            totalCols = Math.Max(viewCols, pluginMaxCol);                 // 列固定视口，插件越界则扩展
            int iconRows = (int)Math.Ceiling(_items.Count / (double)totalCols);
            totalRows = Math.Max(viewRows, Math.Max(iconRows, pluginMaxRow));
        }
        totalRows = Math.Max(totalRows, 1);
        totalCols = Math.Max(totalCols, 1);

        // 设置行列定义
        IconGrid.RowDefinitions.Clear();
        IconGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < totalRows; i++)
            IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
        for (int i = 0; i < totalCols; i++)
            IconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconCell) });

        IconGrid.Children.Clear();

        // 构建占位矩阵（true=已占用）
        var occupied = new bool[totalRows, totalCols];

        // 先放置插件到预设位置（插件可能占据更大空间，优先分配）
        foreach (var plugin in expandedPlugins)
        {
            int pRow = plugin.GridRow;
            int pCol = plugin.GridColumn;
            int pRowSpan = plugin.GridRowSpan;
            int pColSpan = plugin.GridColSpan;

            // 如果位置无效或已占用，寻找新位置
            if (pRow < 0 || pCol < 0 || pRow + pRowSpan > totalRows || pCol + pColSpan > totalCols
                || IsAreaOccupied(occupied, Math.Max(0, pRow), Math.Max(0, pCol), pRowSpan, pColSpan))
            {
                var pos = FindFreePosition(occupied, totalRows, totalCols, pRowSpan, pColSpan);
                if (pos.row < 0)
                {
                    // 仍放不下则沿滚动方向扩展网格（极少见）
                    GrowGrid(ref totalRows, ref totalCols, horizontal, ref occupied);
                    pos = FindFreePosition(occupied, totalRows, totalCols, pRowSpan, pColSpan);
                }
                pRow = pos.row;
                pCol = pos.col;
                plugin.GridRow = pRow;
                plugin.GridColumn = pCol;
            }

            // 标记占用
            MarkArea(occupied, pRow, pCol, pRowSpan, pColSpan);

            // 渲染插件
            var pluginElement = BuildPluginGridCell(plugin);
            Grid.SetRow(pluginElement, pRow);
            Grid.SetColumn(pluginElement, pCol);
            Grid.SetRowSpan(pluginElement, pRowSpan);
            Grid.SetColumnSpan(pluginElement, pColSpan);
            IconGrid.Children.Add(pluginElement);
        }

        // 位置编码所用的列数：横向滚动时网格列数=totalCols（与存储/读取保持一致，避免索引冲突）；纵向=viewCols
        int posDiv = horizontal ? totalCols : viewCols;

        // 放置快捷方式图标（1x1占位）
        foreach (var item in _items)
        {
            // 查找预设位置
            int targetCell = -1;
            if (_config.ShortcutPositions != null && _config.ShortcutPositions.ContainsKey(item.LinkPath))
                targetCell = _config.ShortcutPositions[item.LinkPath];

            int row, col;
            if (targetCell >= 0)
            {
                row = targetCell / posDiv;
                col = targetCell % posDiv;
                // 如果预设位置无效或已被占用，寻找新位置
                if (row >= totalRows || col >= totalCols || occupied[row, col])
                {
                    var pos = FindFreePosition(occupied, totalRows, totalCols, 1, 1);
                    if (pos.row < 0) { GrowGrid(ref totalRows, ref totalCols, horizontal, ref occupied); pos = FindFreePosition(occupied, totalRows, totalCols, 1, 1); }
                    row = pos.row;
                    col = pos.col;
                }
            }
            else
            {
                var pos = FindFreePosition(occupied, totalRows, totalCols, 1, 1);
                if (pos.row < 0) { GrowGrid(ref totalRows, ref totalCols, horizontal, ref occupied); pos = FindFreePosition(occupied, totalRows, totalCols, 1, 1); }
                row = pos.row;
                col = pos.col;
            }

            // 确保字典不为 null
            if (_config.ShortcutPositions == null) _config.ShortcutPositions = new();
            occupied[row, col] = true;
            _config.ShortcutPositions[item.LinkPath] = row * posDiv + col;

            var cell = BuildCell(item);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            // 边缘行放大向内生长，避免被 ScrollViewer 裁切：单行/顶排向下、底排向上、中间保持中心
            if (totalRows <= 1 || row == 0) cell.RenderTransformOrigin = new WpfPoint(0.5, 0);
            else if (row == totalRows - 1) cell.RenderTransformOrigin = new WpfPoint(0.5, 1);
            IconGrid.Children.Add(cell);
        }

        // 滚动方向：只启用选定轴向，关闭另一轴（横向：底部横向条；纵向：右侧纵向条）
        if (IconScroller != null)
        {
            // 边缘"虚化"：以「ScrollViewer 实际可滚动量」为准，而非「设定行列」——
            // 因为 RecomputeTargets 会把面板按工作区夹紧，靠近屏幕边缘的文件夹真实可视行/列少于设定值，
            // 此时实际可滚动但按设定行列判定为不溢出会漏掉淡入淡出。用 OpacityMask 让边缘图标淡出成透明
            // （真正的虚化，非黑色遮罩带）；阴影已移至并列的 PanelShadow，故 OpacityMask 可正常生效。
            // BuildGrid 时布局尚未计算（ScrollableHeight 仍为 0），故布局完成后再复核一次。
            UpdateScrollFade();
            IconScroller.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(UpdateScrollFade));

            if (horizontal)
            {
                IconScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                IconScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                IconScroller.PanningMode = PanningMode.HorizontalOnly;
            }
            else
            {
                IconScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                IconScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                IconScroller.PanningMode = PanningMode.VerticalOnly;
            }
        }

        CollectOrderedCells();
    }

    /// <summary>收集展开网格中的快捷方式格子，按行优先（上→下、左→右）排序，供展开动画错峰淡入使用。</summary>
    private void CollectOrderedCells()
    {
        _orderedCells.Clear();
        var list = new List<(Border B, int R, int C)>();
        foreach (var child in IconGrid.Children)
        {
            if (child is Border b && b.GetValue(DragIdProperty) is string s && s == "shortcut")
                list.Add((b, Grid.GetRow(b), Grid.GetColumn(b)));
        }
        list.Sort((a, b) => (a.R * 1000 + a.C).CompareTo(b.R * 1000 + b.C));
        _orderedCells.AddRange(list.Select(x => x.B));
    }

    /// <summary>按 ScrollViewer 真实可滚动量，用 OpacityMask 让边缘图标淡出成透明（真正的"虚化"）：
    /// 滚动时（ScrollableHeight/Width&gt;1）在对应轴向上加一段 alpha 渐变遮罩，图标经过边缘平滑淡出；
    /// 不滚动则清除遮罩。遮罩固定在 ScrollViewer 视口坐标系，内容滚动时图标穿越遮罩边界自然淡入淡出。</summary>
    private void UpdateScrollFade()
    {
        if (IconScroller == null) return;
        bool scrollable = IconScroller.ScrollableHeight > 1.0 || IconScroller.ScrollableWidth > 1.0;
        if (!scrollable) { IconScroller.OpacityMask = null; return; }

        bool horizontal = _scrollHorizontal;
        // alpha 渐变：边缘透明(0)→内部不透明(1)，仅边缘薄薄一段淡出，图标本体仍清晰
        double edge = horizontal ? 0.07 : 0.12;
        var mask = new LinearGradientBrush
        {
            StartPoint = horizontal ? new WpfPoint(0, 0) : new WpfPoint(0, 0),
            EndPoint   = horizontal ? new WpfPoint(1, 0) : new WpfPoint(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0,   0, 0, 0), 0.0),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), edge),
                new GradientStop(Color.FromArgb(255, 0, 0, 0), 1.0 - edge),
                new GradientStop(Color.FromArgb(0,   0, 0, 0), 1.0)
            }
        };
        IconScroller.OpacityMask = mask;
    }

    /// <summary>在指定滚动方向上为网格扩展一行/一列，并同步占位矩阵与 IconGrid 的行列定义。</summary>
    private void GrowGrid(ref int totalRows, ref int totalCols, bool horizontal, ref bool[,] occupied)
    {
        if (horizontal)
        {
            totalCols++;
            var ns = new bool[totalRows, totalCols];
            for (int r = 0; r < totalRows; r++)
                for (int c = 0; c < totalCols - 1; c++)
                    ns[r, c] = occupied[r, c];
            occupied = ns;
            IconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconCell) });
        }
        else
        {
            totalRows++;
            var ns = new bool[totalRows, totalCols];
            for (int r = 0; r < totalRows - 1; r++)
                for (int c = 0; c < totalCols; c++)
                    ns[r, c] = occupied[r, c];
            occupied = ns;
            IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
        }
    }

    /// <summary>检查指定区域是否已被占用</summary>
    private static bool IsAreaOccupied(bool[,] occupied, int row, int col, int rowSpan, int colSpan)
    {
        for (int r = row; r < row + rowSpan; r++)
            for (int c = col; c < col + colSpan; c++)
                if (occupied[r, c]) return true;
        return false;
    }

    /// <summary>标记指定区域为已占用</summary>
    private static void MarkArea(bool[,] occupied, int row, int col, int rowSpan, int colSpan)
    {
        for (int r = row; r < row + rowSpan; r++)
            for (int c = col; c < col + colSpan; c++)
                occupied[r, c] = true;
    }

    /// <summary>查找第一个空闲位置</summary>
    private static (int row, int col) FindFreePosition(bool[,] occupied, int rows, int cols, int rowSpan, int colSpan)
    {
        for (int r = 0; r <= rows - rowSpan; r++)
            for (int c = 0; c <= cols - colSpan; c++)
                if (!IsAreaOccupied(occupied, r, c, rowSpan, colSpan))
                    return (r, c);
        // 如果找不到，返回 (-1, 0) 表示需要扩展
        return (-1, 0);
    }

    /// <summary>构建网格中的插件单元格（支持拖拽）</summary>
    private FrameworkElement BuildPluginGridCell(FolderPlugin plugin)
    {
        double cellW = IconCell * plugin.GridColSpan;
        double cellH = IconCell * plugin.GridRowSpan;
        // 让插件内容填满整个单元格区域
        double size = Math.Min(cellW, cellH);

        var inner = BuildPluginContent(plugin, size);

        var wrapper = new Border
        {
            Width = cellW,
            Height = cellH,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = plugin.Type.ToString(),
            Child = new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                Child = inner
            }
        };

        // 设置插件标识用于拖拽
        wrapper.SetValue(DragIdProperty, plugin.GridId);
        wrapper.SetValue(DragTypeProperty, "plugin");

        // 拖拽支持
        bool isDragging = false;
        wrapper.MouseLeftButtonDown += (sender, e) =>
        {
            _dragSource = (FrameworkElement)sender;
            _dragItemId = plugin.GridId;
            _dragItemType = "plugin";
            _dragStartPoint = e.GetPosition(IconGrid);
            _dragItemWidth = cellW;
            _dragItemHeight = cellH;
            isDragging = true;
            wrapper.CaptureMouse();
        };

        wrapper.MouseMove += (sender, e) =>
        {
            if (!isDragging || _gridItemDragging) return;
            var pos = e.GetPosition(IconGrid);
            double dist = Math.Sqrt(Math.Pow(pos.X - _dragStartPoint.X, 2) + Math.Pow(pos.Y - _dragStartPoint.Y, 2));
            if (dist > 5)
            {
                _gridItemDragging = true;
                OnGridDragBegin(); // 拖拽开始：取消名称浮层、Dock 放大归零
                // 释放鼠标捕获，让 DoDragDrop 接管
                if (wrapper.IsMouseCaptured) wrapper.ReleaseMouseCapture();

                var data = new DataObject();
                data.SetData("DragId", _dragItemId);
                data.SetData("DragType", _dragItemType);
                data.SetData("DragSource", _dragSource);
                // 跨文件夹拖拽标识：包含源文件夹配置
                data.SetData("DeskFolderPlugin", $"{_config.Id}:{_dragItemId}");
                System.Windows.DragDrop.DoDragDrop(wrapper, data, System.Windows.DragDropEffects.Move);

                // 拖拽完成（无论成功或取消）后清理状态
                isDragging = false;
                _dragSource = null;
                _dragItemId = null;
                _dragItemType = null;
                _gridItemDragging = false;
                RemoveDropIndicator();
            }
        };

        wrapper.MouseLeftButtonUp += (_, _) =>
        {
            isDragging = false;
            if (wrapper.IsMouseCaptured) wrapper.ReleaseMouseCapture();
        };

        wrapper.MouseEnter += (_, _) =>
        {
            if (!_gridItemDragging)
                wrapper.Background = new SolidColorBrush(Color.FromArgb(0x35, 0x78, 0xD7, 0));
        };
        wrapper.MouseLeave += (_, _) =>
        {
            if (!_gridItemDragging)
                wrapper.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        };

        _pluginVisuals.Add(wrapper);
        return wrapper;
    }

    /// <summary>构建网格中的快捷方式单元格（支持拖拽）。
    /// 主题开启「隐藏软件名称」时：不创建名称 TextBlock、图标放大为 IconSizeNoName、移除默认 ToolTip，
    /// 改为悬停延迟后在图标上方浮出名称标签。</summary>
    private UIElement BuildCell(ShortcutItem item)
    {
        bool hideNames = S.GetThemeForFolder(_config.FolderThemeId).HideShortcutNames;
        double icon = hideNames ? IconSizeNoName : Math.Max(IconSize, 34);
        double cellW = IconCell, cellH = IconCell;

        var img = new System.Windows.Controls.Image
        {
            Width = icon, Height = icon,
            Source = item.Icon,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var textColor = CurrentTextColor();
        TextBlock? text = null;
        if (!hideNames)
        {
            text = new TextBlock
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
        }
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var border = new Border
        {
            Width = cellW - 6,
            Height = cellH - 6,
            CornerRadius = new CornerRadius(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            // 隐藏名称模式下移除默认 ToolTip（避免与悬停名称浮层重复显示）
            ToolTip = hideNames ? null : item.Name,
            Child = stack
        };
        stack.Children.Add(img);
        if (text != null) stack.Children.Add(text);

        // 设置图标标识用于拖拽
        border.SetValue(DragIdProperty, item.LinkPath);
        border.SetValue(DragTypeProperty, "shortcut");

        // Dock 放大：中心原点 ScaleTransform（RenderTransform，绝不引起重新布局）；仅快捷方式格子参与
        var magnifyScale = new ScaleTransform(1, 1);
        border.RenderTransform = magnifyScale;
        border.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        _magnifyCells.Add(new MagnifyCell { Cell = border, Scale = magnifyScale });

        // 隐藏名称模式：悬停延迟浮出名称标签；MouseLeave / 点击 / 拖拽开始立即取消
        if (hideNames)
        {
            border.MouseEnter += (_, _) => BeginHoverNameDelay(border, item.Name);
            border.MouseLeave += (_, _) => CancelHoverName();
            border.MouseLeftButtonDown += (_, _) => CancelHoverName();
        }

        // 拖拽支持
        bool isDragging = false;
        bool dragStarted = false;
        border.MouseLeftButtonDown += (sender, e) =>
        {
            _dragSource = (FrameworkElement)sender;
            _dragItemId = item.LinkPath;
            _dragItemType = "shortcut";
            _dragStartPoint = e.GetPosition(IconGrid);
            _dragItemWidth = cellW;
            _dragItemHeight = cellH;
            isDragging = true;
            dragStarted = false;
            border.CaptureMouse();
        };

        border.MouseMove += (sender, e) =>
        {
            if (!isDragging || _gridItemDragging) return;
            var pos = e.GetPosition(IconGrid);
            double dist = Math.Sqrt(Math.Pow(pos.X - _dragStartPoint.X, 2) + Math.Pow(pos.Y - _dragStartPoint.Y, 2));
            if (dist > 5)
            {
                dragStarted = true;
                _gridItemDragging = true;
                OnGridDragBegin(); // 拖拽开始：取消名称浮层、Dock 放大归零
                // 释放鼠标捕获，让 DoDragDrop 接管
                if (border.IsMouseCaptured) border.ReleaseMouseCapture();

                var data = new DataObject();
                data.SetData("DragId", _dragItemId);
                data.SetData("DragType", _dragItemType);
                data.SetData("DragSource", _dragSource);
                System.Windows.DragDrop.DoDragDrop(border, data, System.Windows.DragDropEffects.Move);

                // 拖拽完成后清理状态
                isDragging = false;
                _dragSource = null;
                _dragItemId = null;
                _dragItemType = null;
                _gridItemDragging = false;
                RemoveDropIndicator();
            }
        };

        border.MouseLeftButtonUp += (_, _) =>
        {
            isDragging = false;
            if (border.IsMouseCaptured) border.ReleaseMouseCapture();
            if (!dragStarted)
            {
                ShortcutService.Launch(item);
                Collapse();
            }
        };

        border.MouseEnter += (_, _) => border.Background =
            new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x78, 0xD7));
        border.MouseLeave += (_, _) => border.Background = System.Windows.Media.Brushes.Transparent;

        return border;
    }

    // 拖拽附加属性
    private static readonly DependencyProperty DragIdProperty =
        DependencyProperty.RegisterAttached("DragId", typeof(string), typeof(FolderWindow));
    private static readonly DependencyProperty DragTypeProperty =
        DependencyProperty.RegisterAttached("DragType", typeof(string), typeof(FolderWindow));

    // ---------------- 展开 / 收起 ----------------

    /// <summary>动画期间窗口应取的最小尺寸：必须同时装下「当前面板」与「折叠图标」两者（取较大值），
    /// 否则当折叠尺寸大于展开尺寸时，折叠图标超出窗口的部分会被裁切（收起动画末尾才突然弹出）。</summary>
    private double AnimWindowW() => Math.Max(_panelTargetW, CollapsedW) + WIN_PAD * 2;
    private double AnimWindowH() => Math.Max(_panelTargetH, CollapsedH) + WIN_PAD * 2;

    /// <summary>按展开排列设置（行×列）计算展开后面板的最终尺寸：面板严格贴合「设定行列」，
    /// 图标超出行列的部分由 ScrollViewer 滚动显示（纵向滚动→行向下增长，横向滚动→列向右增长）。
    /// 锚点始终是折叠图标左上角，仅受工作区右/下剩余空间约束（空间不足时夹紧，滚动条兜底）。</summary>
    private void RecomputeTargets()
    {
        var wa = SystemParameters.WorkArea;
        int cols = Math.Max(1, EffectiveCols);
        int rows = Math.Max(1, EffectiveRows);

        // 面板尺寸严格等于「设定行列」对应的像素（不再被图标数量撑大，实现"刚好符合排列设置"）
        double contentW = cols * IconCell;
        double contentH = rows * IconCell;
        double pw = contentW + PanelPaddingH;
        double ph = contentH + HeaderHeight + PanelPaddingV;

        // 仅受工作区右/下剩余空间约束（不足则夹紧，由 ScrollViewer 兜底显示溢出图标）
        double maxPW = (wa.Right - (_collapsedLeft - WIN_PAD)) - WIN_PAD * 2;
        double maxPH = (wa.Bottom - (_collapsedTop - WIN_PAD)) - WIN_PAD * 2;
        _panelTargetW = Math.Min(pw, maxPW);
        _panelTargetH = Math.Min(ph, maxPH);
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

        Width = AnimWindowW();
        Height = AnimWindowH();
        if (IsEdgeFolder()) ApplyEdgePosition(true); // 展开态按 EdgeAnchor 贴边并保留 EdgeDistance 间距

        CollapsedView.IsHitTestVisible = false;
        Panel.Visibility = Visibility.Visible;
        _slotExpanded?.GifTimer?.Start();
        Panel.Width = CollapsedW;
        Panel.Height = CollapsedH;

        PluginHostCollapsed.Visibility = Visibility.Visible;
        PluginHostCollapsed.Opacity = 1;
        AnimateTo(expand: true, d.AnimationMs);

        // 如果有音乐播放器插件，更新折叠态UI的歌曲信息（展开态UI将在动画完成后延迟创建）
        if (_musicPlayerPlugin != null)
        {
            try { UpdateMusicPlayerUI(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeskFolder] Expand UpdateMusicPlayerUI error: {ex.Message}");
            }
        }
    }

    private void Collapse()
    {
        if (!_expanded || _animating || _contextMenuOpen || _settingsOpen) return;

        if (_musicPinned && _musicPlayerPlugin != null)
        {
            return;
        }

        _animating = true;
        _collapseTimer.Stop();
        HideHoverNameLabel(); // 收起时隐藏可能残留的名称浮层
        CollapsedView.Visibility = Visibility.Visible;
        int ms = Math.Max((int)(S.Data.AnimationMs * 1.5), 300);

        // 先清理展开态音乐UI，避免动画期间的引用问题
        if (_musicPlayerExpanded != null && Panel != null)
        {
            try
            {
                var innerGrid = Panel.Child as Grid;
                if (innerGrid != null)
                {
                    innerGrid.Children.Remove(_musicPlayerExpanded);
                }
            }
            catch { }

            _musicPlayerExpanded = null;

            // 清除展开态UI元素引用
            _musicExpandedTitle = null;
            _musicExpandedArtist = null;
            _musicExpandedPlayPauseBtn = null;
            _musicExpandedPrevBtn = null;
            _musicExpandedNextBtn = null;
            _musicPinBtn = null;
            _musicLyricsScroll = null;
            _musicLyricsPanel = null;
            _musicLyricLineElements.Clear();
            _lyricsAnimActive = false; // 展开态歌词UI已销毁，停掉在途滚动动画
        }
        if (IconScroller != null)
        {
            IconScroller.Visibility = Visibility.Visible;
        }

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
        // 展开/收起时切换视频播放：目标槽播放、另一槽暂停解码（隐藏槽不再占用 CPU/内存）
        SyncVideoPlayback(expand);
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
            PluginHostCollapsed.Opacity = 1;  // 展开时插件从1渐隐到0
            // 展开图标错峰出现：先把所有图标设为透明，OnPanelAnimFrame 按行优先逐个淡入
            foreach (var c in _orderedCells) c.Opacity = 0;
        }
        else
        {
            _panelFromW = _panelTargetW;   _panelFromH = _panelTargetH;
            _panelToW = CollapsedW;        _panelToH = CollapsedH;
            Panel.Opacity = 1;
            CollapsedView.Opacity = 0;
            // 收起前先显示插件宿主，让它能参与透明度动画
            PluginHostCollapsed.Visibility = Visibility.Visible;
            PluginHostCollapsed.Opacity = 0;  // 收起时插件从0渐显到1
        }

        CompositionTarget.Rendering -= OnRenderFrame;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        // 展开/收起面板动画、歌词滚动动画与 Dock 放大动画共用本订阅：各自推进，全部结束才退订（不留常驻钩子）。
        // 面板部分由 _animating 门控；歌词/放大动画各自由 _lyricsAnimActive / _magnifyActive 门控，互不误触。
        if (_animating)
            OnPanelAnimFrame();
        OnLyricsAnimFrame();
        if (_magnifyActive)
            OnMagnifyFrame();
        OnIconScrollFrame();
        if (!_animating && !_lyricsAnimActive && !_magnifyActive && !_iconScrollActive)
            CompositionTarget.Rendering -= OnRenderFrame;
    }

    /// <summary>展开/收起面板动画帧推进（原 OnRenderFrame 面板部分，逻辑不变）</summary>
    private void OnPanelAnimFrame()
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - _animStartTicks) / (double)Stopwatch.Frequency * 1000.0;
        double p = Math.Min(1.0, elapsedMs / _animMs);
        double k = EaseInOutCubic(p); // 使用更平滑的 InOutCubic 缓动

        // 面板宽、高由同一进度驱动、同帧赋值；起止尺寸按展开/收起方向确定 → 宽高严格同步且方向正确
        Panel.Width = _panelFromW + (_panelToW - _panelFromW) * k;
        Panel.Height = _panelFromH + (_panelToH - _panelFromH) * k;

        if (_animExpand)
        {
            CollapsedView.Opacity = 1 - k;
            Panel.Opacity = k;
            PluginHostCollapsed.Opacity = 1 - k; // 折叠态插件同步淡出
        }
        else
        {
            CollapsedView.Opacity = k;
            Panel.Opacity = 1 - k;
            PluginHostCollapsed.Opacity = k; // 折叠态插件同步淡入
        }

        // 展开图标错峰淡入：按行优先（上→下、左→右）索引逐个出现（所有主题通用）
        if (_animExpand && _orderedCells.Count > 0)
        {
            int n = _orderedCells.Count;
            double cellFade = 0.35;           // 单个图标淡入占整体动画的比例
            double maxStart = 1 - cellFade;   // 最后一个图标最晚开始时刻
            double denom = Math.Max(1, n - 1);
            for (int i = 0; i < n; i++)
            {
                double start = maxStart * i / denom;
                double o = (p - start) / cellFade;
                _orderedCells[i].Opacity = Math.Max(0, Math.Min(1, o));
            }
        }

        if (p >= 1.0)
        {
            // 退订由 OnRenderFrame 末尾统一处理（歌词动画可能仍在进行）
            _animating = false;

            if (_animExpand)
            {
                // 精确落到目标值，避免浮点残差
                Panel.Width = _panelTargetW;
                Panel.Height = _panelTargetH;
                Panel.Opacity = 1;
                foreach (var c in _orderedCells) c.Opacity = 1; // 动画结束确保图标完全显示
                CollapsedView.Visibility = Visibility.Collapsed;
                CollapsedView.Opacity = 1;
                PluginHostCollapsed.Opacity = 0;
                PluginHostCollapsed.Visibility = Visibility.Collapsed;
                if (!IsMouseOver) _collapseTimer.Start();

                // 如果有音乐播放器插件，延迟到动画帧之后创建展开态UI，避免卡顿
                if (_musicPlayerPlugin != null && Panel != null)
                {
                    // 使用 BeginInvoke 延迟一帧，让动画完成后再创建大量 UI 元素
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_musicPlayerPlugin == null || Panel == null || !_expanded) return;

                            var innerGrid = Panel.Child as Grid;
                            if (innerGrid == null) return;

                            // 移除旧的音乐播放器展开UI
                            if (_musicPlayerExpanded != null)
                            {
                                innerGrid.Children.Remove(_musicPlayerExpanded);
                            }
                            // 隐藏图标网格
                            if (IconScroller != null) IconScroller.Visibility = Visibility.Collapsed;

                            // 创建并添加音乐播放器展开UI
                            var musicExpanded = BuildMusicPlayerExpanded(Panel.ActualWidth, Panel.ActualHeight);
                            innerGrid.Children.Add(musicExpanded);
                            Grid.SetColumn(musicExpanded, 0);
                            Grid.SetColumnSpan(musicExpanded, 2);
                            Grid.SetRow(musicExpanded, 0);
                            Grid.SetRowSpan(musicExpanded, 3);

                            // 立即更新展开态UI的歌曲信息
                            UpdateMusicPlayerUI();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DeskFolder] BuildMusicPlayerExpanded deferred error: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
            else
            {
                Panel.Visibility = Visibility.Collapsed;
                _slotExpanded?.GifTimer?.Stop(); // 面板隐藏时暂停其 GIF 动画，避免后台空转
                Panel.Opacity = 1;
                Panel.Width = CollapsedW;
                Panel.Height = CollapsedH;
                CollapsedView.Opacity = 1;
                CollapsedView.IsHitTestVisible = true;
                PluginHostCollapsed.Opacity = 1;
                PluginHostCollapsed.Visibility = Visibility.Visible; // 收起时显示折叠态插件
                // 收起完成：窗口一次性缩回折叠尺寸（仅此一次跳变，无残影）
                Width = CollapsedW + WIN_PAD * 2;
                Height = CollapsedH + WIN_PAD * 2;
                if (IsEdgeFolder()) ApplyEdgePosition(false); // 收起后重新贴边
                _expanded = false;
                if (IsMouseOver)
                {
                    _hoverTimer.Interval = TimeSpan.FromMilliseconds(S.Data.HoverDelayMs);
                    _hoverTimer.Start();
                }
            }
        }
    }

    // 使用更平滑的 InOutCubic 缓动函数
    private static double EaseInOutCubic(double p)
    {
        return p < 0.5
            ? 4 * p * p * p
            : 1.0 - Math.Pow(-2 * p + 2, 3) / 2.0;
    }

    // EaseOutCubic 缓动：起步快、收尾缓，用于歌词换行平滑滚动
    private static double EaseOutCubic(double p)
    {
        return 1.0 - Math.Pow(1.0 - p, 3);
    }

    /// <summary>启动歌词平滑滚动动画：从当前实际偏移滚到目标偏移（EaseOutCubic，帧驱动）。
    /// 动画进行中重定向时，从当前实际位置起新动画，不跳变、不叠钩子。</summary>
    private void StartLyricsScrollAnimation(double targetOffset)
    {
        if (_musicLyricsScroll == null) return;

        _lyricsAnimFrom = _musicLyricsScroll.VerticalOffset; // 动画在途时此为上一帧实际位置
        _lyricsAnimTo = targetOffset;

        // 目标与当前位置基本一致：无需动画，直接落位
        if (Math.Abs(_lyricsAnimTo - _lyricsAnimFrom) < 0.5)
        {
            _lyricsAnimActive = false;
            _musicLyricsScroll.ScrollToVerticalOffset(targetOffset);
            return;
        }

        _lyricsAnimStartTicks = Stopwatch.GetTimestamp();
        _lyricsAnimActive = true;
        // 与面板动画共用 OnRenderFrame；先减后加避免重复订阅
        CompositionTarget.Rendering -= OnRenderFrame;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    /// <summary>歌词平滑滚动动画帧推进；结束或滚动控件已销毁即停止（退订由 OnRenderFrame 统一处理）。</summary>
    private void OnLyricsAnimFrame()
    {
        if (!_lyricsAnimActive) return;

        if (_musicLyricsScroll == null)
        {
            _lyricsAnimActive = false;
            return;
        }

        double elapsedMs = (Stopwatch.GetTimestamp() - _lyricsAnimStartTicks) / (double)Stopwatch.Frequency * 1000.0;
        double p = Math.Min(1.0, elapsedMs / LyricsAnimMs);
        double k = EaseOutCubic(p);
        _musicLyricsScroll.ScrollToVerticalOffset(_lyricsAnimFrom + (_lyricsAnimTo - _lyricsAnimFrom) * k);
        if (p >= 1.0)
            _lyricsAnimActive = false;
    }

    /// <summary>启动图标面板滚轮平滑滚动：从当前实际偏移缓动到目标偏移（EaseOutCubic，帧驱动）。
    /// 动画在途时再来滚轮事件：从当前实际位置重定向到新目标，不跳变、不叠钩子（仿歌词滚动）。</summary>
    private void StartIconScrollAnimation(double targetOffset, bool horizontal)
    {
        if (IconScroller == null) return;

        _iconScrollHorizontal = horizontal;
        // 从当前实际偏移起算（动画在途时为上一帧实际位置），保证重定向平滑无跳变
        double from = horizontal ? IconScroller.HorizontalOffset : IconScroller.VerticalOffset;
        _iconScrollFrom = from;
        _iconScrollTo = targetOffset;

        // 目标与当前位置基本一致：无需动画，直接落位
        if (Math.Abs(_iconScrollTo - _iconScrollFrom) < 0.5)
        {
            _iconScrollActive = false;
            if (horizontal) IconScroller.ScrollToHorizontalOffset(targetOffset);
            else IconScroller.ScrollToVerticalOffset(targetOffset);
            return;
        }

        _iconScrollStartTicks = Stopwatch.GetTimestamp();
        _iconScrollActive = true;
        // 与面板/歌词/放大动画共用 OnRenderFrame；先减后加避免重复订阅
        CompositionTarget.Rendering -= OnRenderFrame;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    /// <summary>图标面板滚轮平滑滚动帧推进；结束或滚动控件已销毁即停止（退订由 OnRenderFrame 统一处理）。</summary>
    private void OnIconScrollFrame()
    {
        if (!_iconScrollActive) return;
        if (IconScroller == null) { _iconScrollActive = false; return; }

        double elapsedMs = (Stopwatch.GetTimestamp() - _iconScrollStartTicks) / (double)Stopwatch.Frequency * 1000.0;
        double p = Math.Min(1.0, elapsedMs / IconScrollAnimMs);
        double k = EaseOutCubic(p);
        double offset = _iconScrollFrom + (_iconScrollTo - _iconScrollFrom) * k;
        if (_iconScrollHorizontal)
            IconScroller.ScrollToHorizontalOffset(offset);
        else
            IconScroller.ScrollToVerticalOffset(offset);
        if (p >= 1.0)
            _iconScrollActive = false;
    }

    // ---------------- 隐藏软件名称：悬停延迟名称浮层 ----------------

    /// <summary>隐藏名称模式：MouseEnter 后启动延迟计时，超时在图标上方浮出名称标签（同一时间只有一个）。</summary>
    private void BeginHoverNameDelay(Border cell, string name)
    {
        if (!_expanded || _gridItemDragging) return;
        if (ReferenceEquals(_hoverNameCell, cell)) return; // 同一格子重复进入：保持现状（计时中或已显示）
        HideHoverNameLabel();
        _hoverNameCell = cell;
        _hoverNameText = name;
        _hoverNameTimer.Stop();
        _hoverNameTimer.Start();
    }

    /// <summary>取消悬停延迟并立即隐藏名称浮层（MouseLeave / 点击 / 拖拽开始时调用）。</summary>
    private void CancelHoverName() => HideHoverNameLabel();

    /// <summary>立即停止计时并隐藏名称浮层（BuildGrid 重建 / 收起时也调用，防止浮层引用已销毁的格子）。</summary>
    private void HideHoverNameLabel()
    {
        _hoverNameTimer.Stop();
        if (_hoverNameLabel != null)
        {
            OverlayLayer.Children.Remove(_hoverNameLabel);
            _hoverNameLabel = null;
        }
        _hoverNameCell = null;
    }

    /// <summary>延迟到达：在目标格子的图标上方浮出名称标签。
    /// 浮层挂在窗口级 OverlayLayer（格子之外）：不受 Dock 缩放影响、不被 ScrollViewer 裁剪、不改变网格布局。</summary>
    private void ShowHoverNameLabel()
    {
        var cell = _hoverNameCell;
        if (cell == null || !_expanded || _gridItemDragging) return;
        if (!cell.IsVisible) { _hoverNameCell = null; return; }

        // 圆角深色半透明底 + 名称文字；颜色/投影遵循 CurrentTextColor() 与 IsImageMode() 规则
        var tb = new TextBlock
        {
            Text = _hoverNameText,
            FontSize = 11,
            Foreground = new SolidColorBrush(CurrentTextColor())
        };
        if (IsImageMode())
            tb.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, Opacity = 0.8, ShadowDepth = 0 };
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x14, 0x14)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Child = tb
        };

        // 定位：水平对齐格中心，底边贴在图标上沿（计入当前 Dock 缩放后的图标实际高度）
        label.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        var cellPos = cell.TranslatePoint(new WpfPoint(0, 0), Root);
        double scale = cell.RenderTransform is ScaleTransform st ? st.ScaleX : 1.0;
        double iconExtent = IconSizeNoName * scale;
        double centerX = cellPos.X + cell.ActualWidth / 2;
        double iconTop = cellPos.Y + (cell.ActualHeight - iconExtent) / 2;
        double x = centerX - label.DesiredSize.Width / 2;
        double y = iconTop - label.DesiredSize.Height - 4;
        if (y < 2) y = cellPos.Y + 2; // 首行顶部空间不足时退为浮在格子上沿内侧，避免被窗口边缘裁掉
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);

        OverlayLayer.Children.Add(label);
        _hoverNameLabel = label;
    }

    // ---------------- Dock 放大（展开态鼠标附近图标随距离平滑放大） ----------------

    /// <summary>IconGrid 鼠标移动：按鼠标到各格中心距离更新目标缩放，并确保放大动画帧已挂载。</summary>
    private void IconGrid_MagnifyMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_expanded || _gridItemDragging || _magnifyCells.Count == 0) return;
        _magnifyMouseInside = true;
        UpdateMagnifyTargets(e.GetPosition(IconGrid));
        EnsureMagnifyRunning();
    }

    /// <summary>鼠标离开 IconGrid：全部格子目标归零，平滑缩回 1.0 后动画自动停止。</summary>
    private void IconGrid_MagnifyMouseLeave(object sender, WpfMouseEventArgs e)
    {
        _magnifyMouseInside = false;
        ResetMagnifyTargets();
        EnsureMagnifyRunning();
    }

    /// <summary>鼠标滚轮平滑滚动：横向模式把竖向 delta 映射为横向偏移；纵向模式接管默认竖向滚动。
    /// 两者均经 EaseOutCubic 缓动（StartIconScrollAnimation），不再逐格硬跳；ScrollTo*Offset 会自动夹紧到合法范围。</summary>
    private void IconScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IconScroller == null) return;
        bool horizontal = EffectiveScroll == 1;
        // 向上滚（delta>0）→ 偏移减小：横向=内容左移露出右侧，纵向=内容上移露出上方；与 WPF 默认方向一致
        double current = horizontal ? IconScroller.HorizontalOffset : IconScroller.VerticalOffset;
        StartIconScrollAnimation(current - e.Delta, horizontal);
        e.Handled = true;
    }

    /// <summary>网格项拖拽开始：取消名称浮层，Dock 放大目标全部归零（拖拽进行中暂停放大）。</summary>
    private void OnGridDragBegin()
    {
        CancelHoverName();
        _magnifyMouseInside = false;
        ResetMagnifyTargets();
        EnsureMagnifyRunning();
    }

    /// <summary>按余弦衰减计算各格目标缩放：d=0 时为 1+MaxExtra，d≥R（1.2×IconCell）时归零（目标 1.0）。
    /// 放大越多 Z 序越高，放大格子可覆盖相邻格子（Panel.ZIndex 不引起重新布局）。</summary>
    private void UpdateMagnifyTargets(WpfPoint mouse)
    {
        double radius = MagnifyRadiusFactor * IconCell;
        foreach (var mc in _magnifyCells)
        {
            var center = mc.Cell.TranslatePoint(
                new WpfPoint(mc.Cell.ActualWidth / 2, mc.Cell.ActualHeight / 2), IconGrid);
            double dx = center.X - mouse.X, dy = center.Y - mouse.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            double falloff = d >= radius ? 0 : 0.5 * (1 + Math.Cos(Math.PI * d / radius));
            mc.Target = 1 + MagnifyMaxExtra * falloff;
            System.Windows.Controls.Panel.SetZIndex(mc.Cell, (int)Math.Round((mc.Target - 1) * 100));
        }
    }

    /// <summary>全部格子目标缩放归零（1.0）并还原 Z 序。</summary>
    private void ResetMagnifyTargets()
    {
        foreach (var mc in _magnifyCells)
        {
            mc.Target = 1.0;
            System.Windows.Controls.Panel.SetZIndex(mc.Cell, 0);
        }
    }

    /// <summary>把放大动画挂进 OnRenderFrame 帧调度器（订阅"先减后加"，仿歌词动画模式）。</summary>
    private void EnsureMagnifyRunning()
    {
        if (_magnifyActive || _magnifyCells.Count == 0) return;
        _magnifyActive = true;
        CompositionTarget.Rendering -= OnRenderFrame;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    /// <summary>放大动画帧：每帧当前缩放向目标 lerp（k=0.25），死区 &lt;0.01 直接置目标防永动；
    /// 拖拽进行中 / 鼠标已离开时目标强制为 1.0；全部稳定后停止（标志归 false，参与统一退订）。</summary>
    private void OnMagnifyFrame()
    {
        bool settled = true;
        foreach (var mc in _magnifyCells)
        {
            double target = (_gridItemDragging || !_magnifyMouseInside) ? 1.0 : mc.Target;
            double cur = mc.Scale.ScaleX;
            double next = cur + (target - cur) * MagnifyLerp;
            if (Math.Abs(next - target) < MagnifyDeadZone) next = target;
            else settled = false;
            mc.Scale.ScaleX = next;
            mc.Scale.ScaleY = next;
        }
        if (settled) _magnifyActive = false;
    }

    // ---------------- 鼠标交互（悬停展开 / 移开收起 / 折叠态拖动） ----------------
    // 采用 WPF 原生鼠标事件（MouseEnter / MouseLeave）+ 折叠态手动拖动（CaptureMouse + MouseMove）：不挂任何原生钩子，
    // 因此 WPF 的 IsMouseOver / 鼠标事件完全正常，悬停展开与折叠态拖动都稳定。窗口挂到桌面 WorkerW 后
    // this.DragMove() 不再可靠，故改用 CaptureMouse + MouseMove 手动拖动（out_rel20 行为）。
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
        if (_expanded && !_animating && !_contextMenuOpen && !_settingsOpen && !_gridItemDragging && !_musicPinned)
            _collapseTimer.Start();
    }

    /// <summary>折叠图标按下：捕获鼠标并记起点，进入手动拖动（窗口挂到桌面后 DragMove 不再可靠，故改手动）。</summary>
    private void FolderChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;                 // 展开态不提供拖动（按需求）
        _mouseDown = true;
        _dragScreenStart = PointToScreen(e.GetPosition(this));
        _winLeftStart = Left;
        _winTopStart = Top;
        _dragging = true;
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        FolderChip.CaptureMouse();
    }

    /// <summary>拖动中：按鼠标位移实时更新窗口位置（左上为锚点，与面板同角生长）。
    /// 注意：PointToScreen 返回的是物理屏幕坐标，而 Window.Left/Top 是逻辑坐标，
    /// 故物理位移需除以 DPI 缩放因子还原为逻辑位移，否则在 150% 等高缩放下窗口会跟着鼠标"加速"。</summary>
    private void FolderChip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown) return;
        var cur = PointToScreen(e.GetPosition(this)); // 物理屏幕坐标（绝对，不随窗口位置变化）
        double dx = (cur.X - _dragScreenStart.X) / _dpiScaleX;
        double dy = (cur.Y - _dragScreenStart.Y) / _dpiScaleY;
        if (IsEdgeFolder())
        {
            // 贴边文件夹：仅允许沿贴边方向拖动，垂直/水平被锁定在屏幕边缘
            if (_config.EdgeAnchor == 1) Left = _winLeftStart + dx;   // 顶：仅横向
            else Top = _winTopStart + dy;                            // 左/右：仅纵向
            ClampEdgeAlong();
        }
        else
        {
            Left = _winLeftStart + dx;
            Top = _winTopStart + dy;
        }
    }

    /// <summary>松开：比较位移判定"拖动"或"轻点展开"，并释放鼠标捕获。</summary>
    private void FolderChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        FolderChip.ReleaseMouseCapture();

        bool moved = Math.Abs(Left - _winLeftStart) > 2 || Math.Abs(Top - _winTopStart) > 2;
        if (moved)
        {
            // 拖动→持久化新位置（允许贴合屏幕最上边）
            SyncIconFromWindow();
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
        _dragging = false;
    }

    // ---------------- 展开态中键拖动（整个面板区域均可触发） ----------------
    // 事件挂在 Panel 上：Panel 有 Background 参与命中测试，折叠态 Visibility=Collapsed 自动不接收事件。
    // 中键不激活窗口（不像左键），不会触发 Deactivated → Collapse，也不会导致 Z 序被提升到普通窗口之上。
    // 复用折叠态的 CaptureMouse + MouseMove 手动拖动；MouseMove 直接复用 FolderChip_MouseMove。

    /// <summary>展开态面板鼠标按下：仅响应中键，捕获鼠标并记起点，进入手动拖动。</summary>
    private void Panel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_expanded || _animating || _resizeMode) return;
        _mouseDown = true;
        _dragScreenStart = PointToScreen(e.GetPosition(this));
        _winLeftStart = Left;
        _winTopStart = Top;
        _dragging = true;
        _hoverTimer.Stop();
        _collapseTimer.Stop();
        Panel.CaptureMouse();
    }

    /// <summary>展开态面板鼠标松开：仅响应中键，释放捕获并持久化新位置。</summary>
    private void Panel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_mouseDown) return;
        _mouseDown = false;
        Panel.ReleaseMouseCapture();

        // 拖动→持久化新位置（与折叠态一致：反推折叠图标位置、重新 PlaceWindow、写回配置）
        SyncIconFromWindow();
        if (Top <= 0 && _collapsedTop > 0) _collapsedTop = 0;
        PlaceWindow();
        _config.X = _collapsedLeft;
        _config.Y = _collapsedTop;
        S.Save();

        _dragging = false;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_expanded && !_contextMenuOpen && !_settingsOpen) Collapse();
    }

    // 注：拖动已由 FolderChip_MouseLeftButtonDown 中的 this.DragMove() 处理，不再挂任何 WndProc 钩子。

    // 拖放 .lnk/文件 到文件夹图标上 → 加入文件夹；拖放其他文件夹的插件 → 移动。
    // 同时作为 DragEnter 与 DragOver 处理器：DragOver 必须持续返回正确 Effects，
    // 否则事件会冒泡到 Window_DragOver（对非 DeskFolderPlugin 数据置 None），
    // 导致拖动桌面快捷方式到文件夹时出现"禁止放置"光标（f4d980d 引入的回归）。
    private void FolderChip_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            e.Effects = System.Windows.DragDropEffects.Copy;
        else if (e.Data.GetDataPresent("DeskFolderPlugin"))
            e.Effects = System.Windows.DragDropEffects.Move;
        else
            e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderChip_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files
            && TryAddShortcutFiles(files))
        {
            e.Handled = true;
        }
    }

    /// <summary>把拖入的文件（仅 .lnk 快捷方式）加入当前文件夹；有新增则返回 true 并已保存+刷新。</summary>
    private bool TryAddShortcutFiles(string[] files)
    {
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
        return added;
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        _musicPinned = false; // 取消音乐播放器固定状态
        Collapse();
    }

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
                }
                else if (item is MenuItem mi2 && mi2.Name == "EdgeSettingsMenu")
                {
                    // 贴边设置仅对「贴边文件夹」主题有意义
                    mi2.Visibility = IsEdgeFolder() ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>打开「贴边设置」对话框（仅「贴边文件夹」主题可用），调整贴边位置 / 方框透明度 / 大小 / 圆角 / 距边距离。</summary>
    private void EdgeSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new EdgeSettingsWindow(_config);
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            // 重新应用主题（折叠态白色方框 / 展开态图片背景与定位）并按白框尺寸重设窗口
            ApplyTheme();
            RefreshEdgeVisual();
            if (!IsMouseOver) _collapseTimer.Start();
        };
        win.ShowDialog();
    }

    /// <summary>供「贴边设置」对话框调用：实时把最新的贴边配置反映到折叠方框 / 展开定位上（不落盘）。
    /// 仅重绘折叠白框、按白框重设窗口尺寸并重定位，避免每次拖动滑块都重建图片槽造成卡顿；完整主题重建由对话框关闭时统一执行。</summary>
    public void RefreshEdgeVisual()
    {
        ApplyEdgeCollapsedVisual();
        Width = CollapsedW + WIN_PAD * 2;
        Height = CollapsedH + WIN_PAD * 2;
        ApplyEdgePosition(_expanded);
    }

    private void PluginsMenu_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        var win = new PluginManagerWindow(_config);
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            _settingsOpen = false;
            // 插件配置变化后立即重新渲染当前窗口的插件
            ApplyPlugins();
            // 如果当前是展开状态，需要重新构建网格以更新插件布局
            if (_expanded)
            {
                BuildGrid();
                RecomputeTargets();
                Panel.Width = _panelTargetW;
                Panel.Height = _panelTargetH;
                Width = AnimWindowW();
                Height = AnimWindowH();
            }
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
            FolderChip.Background = System.Windows.Media.Brushes.Transparent;
            Panel.Background = System.Windows.Media.Brushes.Transparent;
            ApplyFrame(FolderChip, theme);
            ApplyFrame(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Image)
        {
            SetupImageThemes(theme);
        }
        else if (theme.Mode == ThemeMode.Gradient)
        {
            ApplyGradientBackground(FolderChip, theme);
            ApplyGradientBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Neon)
        {
            ApplyNeonBackground(FolderChip, theme);
            ApplyNeonBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Glass)
        {
            ApplyGlassBackground(FolderChip, theme);
            ApplyGlassBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Acrylic)
        {
            ApplyAcrylicBackground(FolderChip, theme);
            ApplyAcrylicBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Paper)
        {
            ApplyPaperBackground(FolderChip, theme);
            ApplyPaperBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Emboss)
        {
            ApplyEmbossBackground(FolderChip, theme);
            ApplyEmbossBackground(Panel, theme);
        }
        else if (theme.Mode == ThemeMode.Edge)
        {
            // 展开态同图片主题（背景图 / 白色透明兜底）；折叠态白色方框在 ApplyTheme 末尾由 ApplyEdgeCollapsedVisual 收尾
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

        ApplyBorderClip(FolderChip);
        ApplyBorderClip(Panel);
        ApplyTextSettings(theme);

        // 插件渲染：与主题无关，每次 ApplyTheme 后重新挂载（含刷新时钟/日历等）
        ApplyPlugins();

        // 贴边文件夹：折叠态白色方框（覆盖上面的图片槽 / 名称条渲染）
        if (IsEdgeFolder()) ApplyEdgeCollapsedVisual();

        // 如果当前处于展开状态，需要重新构建网格以更新图标文字颜色等
        if (_expanded && !_animating)
        {
            BuildGrid();
        }
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

    // ===== 主题 4：渐变背景（Gradient） =====
    private void ApplyGradientBackground(Border target, ThemeConfig theme)
    {
        target.Background = Brushes.Transparent;
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;

        ThemeHelper.TryParseColor(theme.GradientColorA, out var ca);
        ThemeHelper.TryParseColor(theme.GradientColorB, out var cb);
        byte alpha = (byte)Math.Clamp(theme.BackgroundOpacity * 255, 0, 255);
        ca.A = alpha; cb.A = alpha;

        Brush brush = theme.GradientType switch
        {
            1 => new RadialGradientBrush(ca, cb) { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 },
            2 => new LinearGradientBrush(ca, cb, new Point(0, 0), new Point(1, 1)), // 对角
            3 => new LinearGradientBrush(ca, cb, new Point(0, 0), new Point(0, 1)), // 垂直
            4 => new LinearGradientBrush(ca, cb, new Point(0, 0), new Point(1, 0)), // 水平
            _ => new LinearGradientBrush(ca, cb, theme.GradientAngle)
        };
        var rect = new Rectangle { Fill = brush, RadiusX = Math.Max(0, theme.CornerRadius), RadiusY = Math.Max(0, theme.CornerRadius), IsHitTestVisible = false };
        Grid.SetRowSpan(rect, rows); Grid.SetColumnSpan(rect, cols);
        grid.Children.Insert(0, rect); _themeVisuals.Add(rect);
    }

    // ===== 主题 5：霓虹风格（Neon） =====
    private void ApplyNeonBackground(Border target, ThemeConfig theme)
    {
        target.Background = Brushes.Transparent;
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        double r = Math.Max(0, theme.CornerRadius);
        double glow = Math.Clamp(theme.NeonGlowIntensity, 0, 3);

        ThemeHelper.TryParseColor(theme.NeonBgColor, out var bgc);
        ThemeHelper.TryParseColor(theme.NeonGlowColor, out var gc);

        // 深色底
        byte bgAlpha = (byte)Math.Clamp(theme.BackgroundOpacity * 255, 0, 255);
        var bgRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(bgAlpha, bgc.R, bgc.G, bgc.B)),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(bgRect, rows); Grid.SetColumnSpan(bgRect, cols);
        grid.Children.Insert(0, bgRect); _themeVisuals.Add(bgRect);

        // 多层发光边框（外扩 3 层模拟 bloom）
        for (int i = 3; i >= 1; i--)
        {
            double inset = -2 * i * glow;
            byte a = (byte)(20 / i);
            var g = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(a, gc.R, gc.G, gc.B)),
                StrokeThickness = 3 * glow,
                Margin = new Thickness(inset),
                RadiusX = Math.Max(0, r - inset), RadiusY = Math.Max(0, r - inset),
                IsHitTestVisible = false
            };
            Grid.SetRowSpan(g, rows); Grid.SetColumnSpan(g, cols);
            grid.Children.Insert(0, g); _themeVisuals.Add(g);
        }
        // 核心亮线
        var core = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, gc.R, gc.G, gc.B)),
            StrokeThickness = 1.5,
            RadiusX = r, RadiusY = r,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(core, rows); Grid.SetColumnSpan(core, cols);
        grid.Children.Insert(0, core); _themeVisuals.Add(core);
    }

    // ===== 主题 6：玻璃拟态（Glass） =====
    private void ApplyGlassBackground(Border target, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        double r = Math.Max(0, theme.CornerRadius);
        double sat = Math.Clamp(theme.GlassSaturation, 0, 1);

        ThemeHelper.TryParseColor(theme.GlassTintColor, out var tint);
        ThemeHelper.TryParseColor(theme.GlassHighlight, out var hl);

        // 磨砂底层（半透明叠色）
        byte a = (byte)Math.Clamp(theme.BackgroundOpacity * 255, 0, 255);
        var baseRect = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(a,
                (byte)(tint.R * sat + 255 * (1 - sat)),
                (byte)(tint.G * sat + 255 * (1 - sat)),
                (byte)(tint.B * sat + 255 * (1 - sat)))),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(baseRect, rows); Grid.SetColumnSpan(baseRect, cols);
        grid.Children.Insert(0, baseRect); _themeVisuals.Add(baseRect);

        // 左上高光边（一条线性渐变表示玻璃反光）
        var hlBrush = new LinearGradientBrush(
            Color.FromArgb(0xB0, hl.R, hl.G, hl.B),
            Color.FromArgb(0x00, hl.R, hl.G, hl.B),
            new Point(0, 0), new Point(0.35, 0.35));
        var hlRect = new Rectangle { Fill = hlBrush, RadiusX = r, RadiusY = r, IsHitTestVisible = false, Opacity = 0.6 };
        Grid.SetRowSpan(hlRect, rows); Grid.SetColumnSpan(hlRect, cols);
        grid.Children.Insert(0, hlRect); _themeVisuals.Add(hlRect);

        // 细边框（与玻璃同色但略深）
        var line = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 0.8,
            RadiusX = r, RadiusY = r,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(line, rows); Grid.SetColumnSpan(line, cols);
        grid.Children.Insert(0, line); _themeVisuals.Add(line);
        target.Background = Brushes.Transparent;
    }

    // ===== 主题 7：亚克力 / Mica Alt（Acrylic） =====
    private void ApplyAcrylicBackground(Border target, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        double r = Math.Max(0, theme.CornerRadius);
        double opacity = Math.Clamp(theme.AcrylicOpacity, 0, 1);
        double noise = Math.Clamp(theme.AcrylicNoise, 0, 0.3);

        ThemeHelper.TryParseColor(theme.AcrylicTint, out var tint);

        // 第一层：模糊底色（叠色）
        byte a = (byte)(opacity * 255);
        var layer1 = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(a, tint.R, tint.G, tint.B)),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(layer1, rows); Grid.SetColumnSpan(layer1, cols);
        grid.Children.Insert(0, layer1); _themeVisuals.Add(layer1);

        // 第二层：浅色混合（Mica Alt 特性）
        var layer2 = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(layer2, rows); Grid.SetColumnSpan(layer2, cols);
        grid.Children.Insert(0, layer2); _themeVisuals.Add(layer2);

        // 第三层：噪点层（用棋盘格+半透明模拟颗粒感，无需 bitmap）
        if (noise > 0.001)
        {
            var noiseRect = new Rectangle
            {
                Fill = new DrawingBrush
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 2, 2),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Drawing = new GeometryDrawing
                    {
                        Brush = new SolidColorBrush(Color.FromArgb((byte)(noise * 255 * 3), 0, 0, 0)),
                        Geometry = new RectangleGeometry(new Rect(0, 0, 1, 1))
                    }
                },
                RadiusX = r, RadiusY = r, IsHitTestVisible = false, Opacity = 1
            };
            Grid.SetRowSpan(noiseRect, rows); Grid.SetColumnSpan(noiseRect, cols);
            grid.Children.Insert(0, noiseRect); _themeVisuals.Add(noiseRect);
        }

        // 细边框
        var frame = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            StrokeThickness = 0.8,
            RadiusX = r, RadiusY = r,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(frame, rows); Grid.SetColumnSpan(frame, cols);
        grid.Children.Insert(0, frame); _themeVisuals.Add(frame);
        target.Background = Brushes.Transparent;
    }

    // ===== 主题 8：折纸风格（Paper） =====
    private void ApplyPaperBackground(Border target, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        double r = Math.Max(0, theme.CornerRadius);
        double depth = Math.Clamp(theme.PaperShadowDepth, 0, 2);

        ThemeHelper.TryParseColor(theme.PaperColor, out var paper);
        Color paperDark = Color.FromRgb(
            (byte)Math.Max(0, paper.R - 40),
            (byte)Math.Max(0, paper.G - 40),
            (byte)Math.Max(0, paper.B - 40));

        // 底层（阴影层）：模拟折在后面的一张纸
        var shadow = new Rectangle
        {
            Fill = new SolidColorBrush(paperDark) { Opacity = 0.35 * depth },
            RadiusX = r, RadiusY = r,
            Margin = theme.PaperFoldDirection switch
            {
                1 => new Thickness(-3 * depth, 3 * depth, 3 * depth, -3 * depth),  // 右上折
                2 => new Thickness(3 * depth, -3 * depth, -3 * depth, 3 * depth),  // 左下折
                3 => new Thickness(-3 * depth, -3 * depth, 3 * depth, 3 * depth),  // 右下折
                _ => new Thickness(3 * depth, 3 * depth, -3 * depth, -3 * depth),  // 默认左上折
            },
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(shadow, rows); Grid.SetColumnSpan(shadow, cols);
        grid.Children.Insert(0, shadow); _themeVisuals.Add(shadow);

        // 纸张主体
        var paperRect = new Rectangle
        {
            Fill = new SolidColorBrush(paper),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(paperRect, rows); Grid.SetColumnSpan(paperRect, cols);
        grid.Children.Insert(0, paperRect); _themeVisuals.Add(paperRect);

        // 折痕线（线性渐变一条暗带）
        Point gp1, gp2;
        switch (theme.PaperFoldDirection)
        {
            case 1: gp1 = new Point(1, 0); gp2 = new Point(0, 1); break;
            case 2: gp1 = new Point(0, 1); gp2 = new Point(1, 0); break;
            case 3: gp1 = new Point(1, 1); gp2 = new Point(0, 0); break;
            default: gp1 = new Point(0, 0); gp2 = new Point(1, 1); break;
        }
        var creaseBrush = new LinearGradientBrush { StartPoint = gp1, EndPoint = gp2 };
        creaseBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
        creaseBrush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0x25 * depth), 0, 0, 0), 0.5));
        creaseBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
        var crease = new Rectangle
        {
            Fill = creaseBrush,
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(crease, rows); Grid.SetColumnSpan(crease, cols);
        grid.Children.Insert(0, crease); _themeVisuals.Add(crease);
        target.Background = Brushes.Transparent;
    }

    // ===== 主题 9：浮雕风格（Emboss） =====
    private void ApplyEmbossBackground(Border target, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return;
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        double r = Math.Max(0, theme.CornerRadius);
        double h = Math.Clamp(theme.EmbossHeight, 0, 8);

        ThemeHelper.TryParseColor(theme.EmbossColor, out var baseCol);
        Color lighter = Color.FromRgb(
            (byte)Math.Min(255, baseCol.R + 40),
            (byte)Math.Min(255, baseCol.G + 40),
            (byte)Math.Min(255, baseCol.B + 40));
        Color darker = Color.FromRgb(
            (byte)Math.Max(0, baseCol.R - 40),
            (byte)Math.Max(0, baseCol.G - 40),
            (byte)Math.Max(0, baseCol.B - 40));

        // 基础背景色
        var baseRect = new Rectangle
        {
            Fill = new SolidColorBrush(baseCol),
            RadiusX = r, RadiusY = r, IsHitTestVisible = false
        };
        Grid.SetRowSpan(baseRect, rows); Grid.SetColumnSpan(baseRect, cols);
        grid.Children.Insert(0, baseRect); _themeVisuals.Add(baseRect);

        // 左上高光边（凸）：用 LinearGradientBrush 沿左上角做亮边
        var hl = new Rectangle
        {
            Stroke = new SolidColorBrush(lighter),
            StrokeThickness = h,
            Margin = new Thickness(h / 2),
            Opacity = 0.8,
            RadiusX = Math.Max(0, r - h / 2), RadiusY = Math.Max(0, r - h / 2),
            IsHitTestVisible = false
        };
        hl.Clip = new RectangleGeometry(new Rect(0, 0, 10000, 10000));
        Grid.SetRowSpan(hl, rows); Grid.SetColumnSpan(hl, cols);
        grid.Children.Insert(0, hl); _themeVisuals.Add(hl);

        // 右下暗边（凸）
        var sh = new Rectangle
        {
            Stroke = new SolidColorBrush(darker),
            StrokeThickness = h,
            Margin = new Thickness(h / 2),
            Opacity = 0.5,
            RadiusX = Math.Max(0, r - h / 2), RadiusY = Math.Max(0, r - h / 2),
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(sh, rows); Grid.SetColumnSpan(sh, cols);
        grid.Children.Insert(0, sh); _themeVisuals.Add(sh);

        // 内阴影凹陷感（上下左右四层淡渐变）
        var innerShade = new Border
        {
            CornerRadius = new CornerRadius(r),
            Background = new RadialGradientBrush(
                Color.FromArgb(0x00, 0, 0, 0),
                Color.FromArgb((byte)(0x18 * (h / 4)), 0, 0, 0))
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.55, RadiusY = 0.55
            },
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(innerShade, rows); Grid.SetColumnSpan(innerShade, cols);
        grid.Children.Insert(0, innerShade); _themeVisuals.Add(innerShade);
        target.Background = Brushes.Transparent;
    }

    // ===== 插件系统：渲染 FolderConfig.Plugins =====
    // 插件是与主题无关的装饰性 UI 元素，可在任意主题的折叠/展开态任意角落插入。
    // 所有插件视觉元素加入 _pluginVisuals；插件计时器加入 _pluginTimers。

    private void ApplyPlugins()
    {
        ClearPlugins();

        // 检测是否包含音乐播放器插件（仅空文件夹生效）
        bool folderEmpty = (_items?.Count ?? 0) == 0;
        _musicPlayerPlugin = folderEmpty ? _config.Plugins?.FirstOrDefault(p => p.Type == FolderPluginType.MusicPlayer) : null;
        if (_musicPlayerPlugin != null)
        {
            InitMusicService();
        }
        else
        {
            CleanupMusicService();
        }

        if (_config.Plugins == null) return;

        // 折叠态：尺寸为 CollapsedW × CollapsedH（FolderChip）
        double cw = CollapsedW, ch = CollapsedH;

        foreach (var p in _config.Plugins)
        {
            if (p.Type == FolderPluginType.None) continue;
            // 音乐播放器插件仅在空文件夹时显示
            if (p.Type == FolderPluginType.MusicPlayer && !folderEmpty) continue;
            if (p.ShowOnCollapsed)
                PluginHostCollapsed.Children.Add(RenderPlugin(p, true, cw, ch));
        }

        // 如果是音乐播放器并且已经展开，延迟创建展开态音乐UI（避免阻塞）
        if (_musicPlayerPlugin != null && _expanded && Panel != null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_musicPlayerPlugin == null || Panel == null || !_expanded) return;

                    var innerGrid = Panel.Child as Grid;
                    if (innerGrid == null) return;

                    // 移除旧的音乐播放器展开UI
                    if (_musicPlayerExpanded != null)
                    {
                        innerGrid.Children.Remove(_musicPlayerExpanded);
                    }
                    // 隐藏原本的内容网格
                    if (IconScroller != null) IconScroller.Visibility = Visibility.Collapsed;

                    var expandedMusic = BuildMusicPlayerExpanded(Panel.ActualWidth, Panel.ActualHeight);
                    innerGrid.Children.Add(expandedMusic);
                    Grid.SetColumn(expandedMusic, 0);
                    Grid.SetColumnSpan(expandedMusic, 2);
                    Grid.SetRow(expandedMusic, 0);
                    Grid.SetRowSpan(expandedMusic, 3);

                    // 立即更新展开态UI的歌曲信息
                    UpdateMusicPlayerUI();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DeskFolder] ApplyPlugins deferred error: {ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void ClearPlugins()
    {
        foreach (var t in _pluginTimers) t.Stop();
        _pluginTimers.Clear();
        PluginHostCollapsed.Children.Clear();
        PluginHostExpanded.Children.Clear();
        _pluginVisuals.Clear();
    }

    // ===== 网格拖拽事件处理 =====

    /// <summary>拖拽经过网格：桌面文件(.lnk)拖入 → 复制加入文件夹；内部图标/插件拖拽 → 移动并显示放置指示</summary>
    private void IconGrid_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        // 桌面文件拖入：显示复制光标，不画内部放置指示
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;

        var pos = e.GetPosition(IconGrid);
        int cols = IconGrid.ColumnDefinitions.Count;
        int rows = IconGrid.RowDefinitions.Count;

        // 计算目标单元格位置
        int col = (int)(pos.X / IconCell);
        int row = (int)(pos.Y / IconCell);
        col = Math.Clamp(col, 0, cols - 1);
        row = Math.Clamp(row, 0, rows - 1);

        _dragOverRow = row;
        _dragOverCol = col;

        // 更新放置指示线（无论 _gridItemDragging 状态）
        UpdateDropIndicator(row, col);
    }

    /// <summary>放置到网格：直接从鼠标位置计算目标格子，支持移动到空位或交换</summary>
    private void IconGrid_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // 桌面文件拖入网格 → 加入文件夹（与折叠态一致）
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            TryAddShortcutFiles(files);
            RemoveDropIndicator();
            _dragOverRow = -1;
            _dragOverCol = -1;
            _gridItemDragging = false;
            e.Handled = true;
            return;
        }

        var dragId = e.Data.GetData("DragId") as string;
        var dragType = e.Data.GetData("DragType") as string;

        if (dragId == null || dragType == null) { RemoveDropIndicator(); return; }

        int cols = IconGrid.ColumnDefinitions.Count;
        int rows = IconGrid.RowDefinitions.Count;

        // 直接从鼠标落点计算目标格子，不依赖 DragOver 设置的变量
        var dropPos = e.GetPosition(IconGrid);
        int targetCol = (int)(dropPos.X / IconCell);
        int targetRow = (int)(dropPos.Y / IconCell);
        targetCol = Math.Clamp(targetCol, 0, Math.Max(0, cols - 1));
        targetRow = Math.Clamp(targetRow, 0, Math.Max(0, rows - 1));

        if (dragType == "plugin")
        {
            var plugin = _config.Plugins?.FirstOrDefault(p => p.GridId == dragId);
            if (plugin == null) { RemoveDropIndicator(); return; }

            int pRowSpan = plugin.GridRowSpan;
            int pColSpan = plugin.GridColSpan;

            // 确保目标位置在网格范围内
            targetCol = Math.Clamp(targetCol, 0, Math.Max(0, cols - pColSpan));
            targetRow = Math.Clamp(targetRow, 0, Math.Max(0, rows - pRowSpan));

            // 简单直接移动到目标位置
            // 如果目标位置被占用，交换位置
            var occupyingPlugin = FindPluginAt(targetRow, targetCol, pRowSpan, pColSpan, plugin.GridId);
            var occupyingIcon = FindIconAt(targetRow, targetCol, pRowSpan, pColSpan, null);

            if (occupyingPlugin != null)
            {
                // 交换两个插件
                int oldRow = plugin.GridRow;
                int oldCol = plugin.GridColumn;
                plugin.GridRow = targetRow;
                plugin.GridColumn = targetCol;
                occupyingPlugin.GridRow = oldRow;
                occupyingPlugin.GridColumn = oldCol;
            }
            else if (occupyingIcon != null)
            {
                // 插件与图标交换
                int oldPluginCell = plugin.GridRow * cols + plugin.GridColumn;
                plugin.GridRow = targetRow;
                plugin.GridColumn = targetCol;
                _config.ShortcutPositions[occupyingIcon.LinkPath] = oldPluginCell;
            }
            else
            {
                // 空位，直接移动
                plugin.GridRow = targetRow;
                plugin.GridColumn = targetCol;
            }
        }
        else if (dragType == "shortcut")
        {
            // 图标拖拽
            int targetCell = targetRow * cols + targetCol;
            int oldCell = _config.ShortcutPositions.ContainsKey(dragId) ? _config.ShortcutPositions[dragId] : -1;

            var occupyingPlugin = FindPluginAt(targetRow, targetCol, 1, 1, null);
            var occupyingIcon = FindIconAt(targetRow, targetCol, 1, 1, dragId);

            if (occupyingPlugin != null)
            {
                // 图标与插件交换
                if (oldCell >= 0)
                {
                    occupyingPlugin.GridRow = oldCell / cols;
                    occupyingPlugin.GridColumn = oldCell % cols;
                }
                _config.ShortcutPositions[dragId] = targetCell;
            }
            else if (occupyingIcon != null && occupyingIcon.LinkPath != dragId)
            {
                // 两个图标交换
                int swapCell = _config.ShortcutPositions.ContainsKey(occupyingIcon.LinkPath)
                    ? _config.ShortcutPositions[occupyingIcon.LinkPath]
                    : -1;

                if (oldCell >= 0)
                    _config.ShortcutPositions[occupyingIcon.LinkPath] = oldCell;

                _config.ShortcutPositions[dragId] = swapCell >= 0 ? swapCell : targetCell;
            }
            else
            {
                // 空位，直接移动
                _config.ShortcutPositions[dragId] = targetCell;
            }
        }

        S.Save();
        RemoveDropIndicator();
        _dragOverRow = -1;
        _dragOverCol = -1;
        _gridItemDragging = false;
        e.Handled = true;

        // 重新构建网格
        BuildGrid();
    }

    /// <summary>查找指定位置占用的插件（排除指定插件）</summary>
    private FolderPlugin? FindPluginAt(int row, int col, int rowSpan, int colSpan, string? excludeId)
    {
        if (_config.Plugins == null) return null;
        foreach (var p in _config.Plugins)
        {
            if (p.Type == FolderPluginType.None) continue;
            if (excludeId != null && p.GridId == excludeId) continue;
            if (p.GridRow < 0 || p.GridColumn < 0) continue;

            int pEndRow = p.GridRow + p.GridRowSpan;
            int pEndCol = p.GridColumn + p.GridColSpan;

            // 检查区域是否重叠
            if (row < pEndRow && row + rowSpan > p.GridRow &&
                col < pEndCol && col + colSpan > p.GridColumn)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>查找指定位置占用的图标</summary>
    private ShortcutItem? FindIconAt(int row, int col, int rowSpan, int colSpan, string? excludeId)
    {
        int cols = IconGrid.ColumnDefinitions.Count;
        int targetCellStart = row * cols + col;
        int targetCellEnd = (row + rowSpan - 1) * cols + (col + colSpan - 1);

        foreach (var kvp in _config.ShortcutPositions)
        {
            if (excludeId != null && kvp.Key == excludeId) continue;
            int cell = kvp.Value;
            if (cell >= targetCellStart && cell <= targetCellEnd)
            {
                return _items.FirstOrDefault(i => i.LinkPath == kvp.Key);
            }
        }
        return null;
    }

    /// <summary>更新放置指示线位置（蓝色=空位，橙色=可交换）</summary>
    private void UpdateDropIndicator(int row, int col)
    {
        int cols = IconGrid.ColumnDefinitions.Count;
        int rows = IconGrid.RowDefinitions.Count;
        int itemColSpan = _dragItemType == "plugin" ? GetPluginColSpan(_dragItemId) : 1;
        int itemRowSpan = _dragItemType == "plugin" ? GetPluginRowSpan(_dragItemId) : 1;

        // 确保不超出边界
        col = Math.Min(col, Math.Max(0, cols - itemColSpan));
        row = Math.Min(row, Math.Max(0, rows - itemRowSpan));
        col = Math.Max(col, 0);
        row = Math.Max(row, 0);

        // 检查目标区域是否被占用
        bool isOccupied = false;
        if (_dragItemType == "plugin")
        {
            var plugin = _config.Plugins?.FirstOrDefault(p => p.GridId == _dragItemId);
            if (plugin != null)
            {
                var occupyingPlugin = FindPluginAt(row, col, itemRowSpan, itemColSpan, plugin.GridId);
                var occupyingIcon = FindIconAt(row, col, itemRowSpan, itemColSpan, null);
                isOccupied = occupyingPlugin != null || occupyingIcon != null;
            }
        }
        else
        {
            var occupyingPlugin = FindPluginAt(row, col, 1, 1, null);
            var occupyingIcon = FindIconAt(row, col, 1, 1, _dragItemId);
            isOccupied = occupyingPlugin != null || occupyingIcon != null;
        }

        // 总是先移除旧的，再创建新的（确保位置正确）
        if (_dropIndicator != null)
        {
            IconGrid.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }

        // 创建新的指示框
        _dropIndicator = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            Margin = new Thickness(2)
        };

        // 根据占用状态设置颜色
        if (isOccupied)
        {
            _dropIndicator.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x8C, 0x00));
            _dropIndicator.BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8C, 0x00));
        }
        else
        {
            _dropIndicator.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x78, 0xD7));
            _dropIndicator.BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x78, 0xD7));
        }

        // 设置位置（使用 Grid 附加属性，不设置固定尺寸让 Grid 自动处理）
        Grid.SetRow(_dropIndicator, row);
        Grid.SetColumn(_dropIndicator, col);
        Grid.SetRowSpan(_dropIndicator, itemRowSpan);
        Grid.SetColumnSpan(_dropIndicator, itemColSpan);
        System.Windows.Controls.Panel.SetZIndex(_dropIndicator, 100);

        IconGrid.Children.Add(_dropIndicator);
        _dropIndicator.InvalidateVisual();
    }

    /// <summary>移除放置指示线</summary>
    private void RemoveDropIndicator()
    {
        if (_dropIndicator != null)
        {
            IconGrid.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }
    }

    /// <summary>获取插件的列跨度</summary>
    private int GetPluginColSpan(string? pluginId)
    {
        if (pluginId == null) return 1;
        var plugin = _config.Plugins?.FirstOrDefault(p => p.GridId == pluginId);
        return plugin?.GridColSpan ?? 1;
    }

    /// <summary>获取插件的行跨度</summary>
    private int GetPluginRowSpan(string? pluginId)
    {
        if (pluginId == null) return 1;
        var plugin = _config.Plugins?.FirstOrDefault(p => p.GridId == pluginId);
        return plugin?.GridRowSpan ?? 1;
    }

    /// <summary>按 corner (0=左上,1=右上,2=左下,3=右下) + 尺寸 + 偏移，将插件放置到目标容器 Grid 中。
    /// 返回包装后的容器，包含实际插件内容。</summary>
    private FrameworkElement RenderPlugin(FolderPlugin p, bool collapsed, double hostW, double hostH)
    {
        double size = p.Size > 10 ? p.Size : PluginDefaultSize(p.Type);
        int corner = collapsed ? p.CollapsedCorner : p.ExpandedCorner;
        double offX = collapsed ? p.CollapsedOffsetX : p.ExpandedOffsetX;
        double offY = collapsed ? p.CollapsedOffsetY : p.ExpandedOffsetY;

        var wrap = new Grid
        {
            Width = hostW,
            Height = hostH,
            IsHitTestVisible = false
        };
        var inner = BuildPluginContent(p, size);

        // 按角位定位
        Thickness m = corner switch
        {
            1 => new Thickness(hostW - size + offX, offY, 0, 0),          // 右上
            2 => new Thickness(offX, hostH - size + offY, 0, 0),          // 左下
            3 => new Thickness(hostW - size + offX, hostH - size + offY, 0, 0), // 右下
            _ => new Thickness(offX, offY, 0, 0)                           // 左上
        };
        inner.Margin = m;
        inner.HorizontalAlignment = HorizontalAlignment.Left;
        inner.VerticalAlignment = VerticalAlignment.Top;

        wrap.Children.Add(inner);
        _pluginVisuals.Add(inner);
        return wrap;
    }

    private static double PluginDefaultSize(FolderPluginType t) => t switch
    {
        FolderPluginType.AnalogClock => 48,
        FolderPluginType.DigitalClock => 96,
        FolderPluginType.StickyNote => 72,
        FolderPluginType.CpuGauge => 54,
        FolderPluginType.WeatherBadge => 52,
        FolderPluginType.CalendarTile => 56,
        FolderPluginType.MusicPlayer => 80, // 和文件夹尺寸相近，比文件夹小4px
        _ => 40
    };

    private FrameworkElement BuildPluginContent(FolderPlugin p, double size)
    {
        return p.Type switch
        {
            FolderPluginType.AnalogClock => BuildAnalogClock(p, size),
            FolderPluginType.DigitalClock => BuildDigitalClock(p, size),
            FolderPluginType.StickyNote => BuildStickyNote(p, size),
            FolderPluginType.CpuGauge => BuildCpuGauge(p, size),
            FolderPluginType.WeatherBadge => BuildWeatherBadge(p, size),
            FolderPluginType.CalendarTile => BuildCalendarTile(p, size),
            FolderPluginType.MusicPlayer => BuildMusicPlayerCollapsed(p, size),
            _ => new Border { Width = size, Height = size }
        };
    }

    // ===== 插件 1：模拟时钟 =====
    private FrameworkElement BuildAnalogClock(FolderPlugin p, double size)
    {
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#333333" : p.Color, out var dialC);

        var canvas = new Canvas { Width = size, Height = size };

        // 表盘外圆
        var rim = new Ellipse
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(dialC), StrokeThickness = size * 0.06
        };
        canvas.Children.Add(rim);

        // 时刻标记：12 点
        for (int i = 0; i < 12; i++)
        {
            double angle = i * 30 * Math.PI / 180.0;
            double cx = size / 2, cy = size / 2;
            double r = size * 0.42;
            double x1 = cx + Math.Sin(angle) * r;
            double y1 = cy - Math.Cos(angle) * r;
            double r2 = size * (i % 3 == 0 ? 0.32 : 0.37);
            double x2 = cx + Math.Sin(angle) * r2;
            double y2 = cy - Math.Cos(angle) * r2;
            var mark = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(dialC),
                StrokeThickness = i % 3 == 0 ? size * 0.05 : size * 0.025,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(mark);
        }

        // 三根指针（作为独立 Shape，通过 RotateTransform 旋转）
        var hour = new Line
        {
            X1 = size / 2, Y1 = size / 2,
            X2 = size / 2, Y2 = size * 0.25,
            Stroke = new SolidColorBrush(dialC),
            StrokeThickness = size * 0.08,
            StrokeEndLineCap = PenLineCap.Round
        };
        var hourRot = new RotateTransform(0, size / 2, size / 2);
        hour.RenderTransform = hourRot;
        canvas.Children.Add(hour);

        var minute = new Line
        {
            X1 = size / 2, Y1 = size / 2,
            X2 = size / 2, Y2 = size * 0.15,
            Stroke = new SolidColorBrush(dialC),
            StrokeThickness = size * 0.055,
            StrokeEndLineCap = PenLineCap.Round
        };
        var minRot = new RotateTransform(0, size / 2, size / 2);
        minute.RenderTransform = minRot;
        canvas.Children.Add(minute);

        var second = new Line
        {
            X1 = size / 2, Y1 = size / 2,
            X2 = size / 2, Y2 = size * 0.12,
            Stroke = Brushes.IndianRed,
            StrokeThickness = size * 0.025,
            StrokeEndLineCap = PenLineCap.Round
        };
        var secRot = new RotateTransform(0, size / 2, size / 2);
        second.RenderTransform = secRot;
        canvas.Children.Add(second);

        // 中心圆点
        var dot = new Ellipse
        {
            Width = size * 0.12, Height = size * 0.12,
            Fill = Brushes.IndianRed
        };
        Canvas.SetLeft(dot, size / 2 - size * 0.06);
        Canvas.SetTop(dot, size / 2 - size * 0.06);
        canvas.Children.Add(dot);

        // 每秒刷新
        void tickClock(object? s, EventArgs e)
        {
            var now = DateTime.Now;
            secRot.Angle = now.Second * 6.0;
            minRot.Angle = now.Minute * 6.0 + now.Second * 0.1;
            hourRot.Angle = (now.Hour % 12) * 30.0 + now.Minute * 0.5;
        }
        tickClock(null, EventArgs.Empty);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += tickClock;
        timer.Start();
        _pluginTimers.Add(timer);

        return canvas;
    }

    // ===== 插件 2：数字时钟 =====
    private FrameworkElement BuildDigitalClock(FolderPlugin p, double size)
    {
        var stack = new StackPanel { Width = size, Height = size * 0.65 };
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#FFFFFF" : p.Color, out var col);
        byte alpha = 0xE6;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B));

        var tTime = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm:ss"),
            Foreground = brush,
            FontSize = size * 0.25,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        stack.Children.Add(tTime);

        var tDate = new TextBlock
        {
            Text = DateTime.Now.ToString("MM-dd ddd"),
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, col.R, col.G, col.B)),
            FontSize = size * 0.13,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        stack.Children.Add(tDate);

        void tick(object? s, EventArgs e)
        {
            tTime.Text = DateTime.Now.ToString("HH:mm:ss");
            tDate.Text = DateTime.Now.ToString("MM-dd ddd");
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += tick;
        timer.Start();
        _pluginTimers.Add(timer);

        var host = new Border
        {
            Width = size,
            Height = size * 0.7,
            CornerRadius = new CornerRadius(size * 0.1),
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
            Child = stack,
            Padding = new Thickness(size * 0.05)
        };
        return host;
    }

    // ===== 插件 3：便签条 =====
    private FrameworkElement BuildStickyNote(FolderPlugin p, double size)
    {
        string text = string.IsNullOrWhiteSpace(p.Text) ? "便签" : p.Text;
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#FFEB3B" : p.Color, out var col);
        var note = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(col),
            CornerRadius = new CornerRadius(0, size * 0.1, size * 0.05, size * 0.1),
            Padding = new Thickness(size * 0.1),
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.3, Color = Colors.Black }
        };
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            FontSize = size * 0.16,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.Medium
        };
        note.Child = tb;

        // 右上角折角：一个小三角形
        var canvas = new Canvas { Width = size, Height = size };
        canvas.Children.Add(note);
        double fold = size * 0.18;
        var poly = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection
            {
                new Point(size - fold, 0),
                new Point(size, 0),
                new Point(size, fold)
            },
            Fill = new SolidColorBrush(Color.FromArgb(0x80, (byte)(col.R * 0.7), (byte)(col.G * 0.7), (byte)(col.B * 0.7)))
        };
        canvas.Children.Add(poly);
        return canvas;
    }

    // ===== 插件 4：CPU 仪表盘（简化为 0-100 刻度，实际用随机+平滑模拟，避免引入 PerformanceCounter 依赖） =====
    private FrameworkElement BuildCpuGauge(FolderPlugin p, double size)
    {
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#00C853" : p.Color, out var col);
        var canvas = new Canvas { Width = size, Height = size };

        // 外圈底
        var bg = new Ellipse
        {
            Width = size, Height = size,
            Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x22, 0x22, 0x22)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = size * 0.03
        };
        canvas.Children.Add(bg);

        double cx = size / 2, cy = size / 2, r = size * 0.38;

        // 刻度弧（270° 量程，从 135° 到 405°）
        var arcPath = new System.Windows.Shapes.Path();
        var geom = new PathGeometry();
        {
            var start = PointOnCircle(cx, cy, r, 135);
            var end = PointOnCircle(cx, cy, r, 135 + 270);
            var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, true, SweepDirection.Clockwise, true));
            geom.Figures.Add(fig);
        }
        arcPath.Data = geom;
        arcPath.Stroke = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        arcPath.StrokeThickness = size * 0.08;
        canvas.Children.Add(arcPath);

        // 动态数值弧：每秒重建一次PathGeometry（1次/秒，GC无压力）
        var fg = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(col),
            StrokeThickness = size * 0.08,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(fg);

        // 中央数字
        var txt = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = size * 0.2,
            FontWeight = FontWeights.Bold,
            Text = "0%"
        };
        canvas.Children.Add(txt);

        // 每秒"采样"（平滑模拟，避免依赖 System.Management）
        double val = 15, target = 25;
        var rnd = new Random();
        int lastPct = -1;
        void tick(object? s, EventArgs e)
        {
            if (rnd.NextDouble() < 0.5) target = rnd.Next(5, 95);
            val = val * 0.65 + target * 0.35;
            int pct = (int)Math.Clamp(val, 0, 100);
            double angleDeg = 135 + pct * 2.7;

            // 只在数值变化时重建，避免无意义的重绘
            if (pct != lastPct)
            {
                lastPct = pct;
                var fgGeom = new PathGeometry();
                var fig = new PathFigure { StartPoint = PointOnCircle(cx, cy, r, 135), IsClosed = false, IsFilled = false };
                fig.Segments.Add(new ArcSegment(
                    PointOnCircle(cx, cy, r, angleDeg),
                    new Size(r, r), 0, angleDeg - 135 > 180, SweepDirection.Clockwise, true));
                fgGeom.Figures.Add(fig);
                fg.Data = fgGeom;
            }

            txt.Text = pct.ToString() + "%";
            txt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = txt.DesiredSize;
            Canvas.SetLeft(txt, size / 2 - sz.Width / 2);
            Canvas.SetTop(txt, size / 2 - sz.Height / 2 + size * 0.04);
        }
        tick(null, EventArgs.Empty);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += tick;
        timer.Start();
        _pluginTimers.Add(timer);

        return canvas;
    }

    // ===== 插件 5：天气徽章（简化显示：图标 + 温度文字，温度来自 FolderPlugin.Text） =====
    private FrameworkElement BuildWeatherBadge(FolderPlugin p, double size)
    {
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#81D4FA" : p.Color, out var bgCol);
        var bg = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new SolidColorBrush(bgCol),
            Padding = new Thickness(size * 0.08)
        };
        var stack = new StackPanel();
        // 图标：用一个 Path 画"云朵+太阳"（简化几何图形）
        double icSz = size * 0.45;
        var sun = new Ellipse
        {
            Width = icSz * 0.65, Height = icSz * 0.65,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
        };
        var cloud1 = new Ellipse
        {
            Width = icSz * 0.5, Height = icSz * 0.35,
            Fill = Brushes.White
        };
        Canvas.SetLeft(cloud1, icSz * 0.3);
        Canvas.SetTop(cloud1, icSz * 0.3);
        var cloud2 = new Ellipse
        {
            Width = icSz * 0.6, Height = icSz * 0.4,
            Fill = Brushes.White
        };
        Canvas.SetLeft(cloud2, icSz * 0.55);
        Canvas.SetTop(cloud2, icSz * 0.35);
        var iconCv = new Canvas { Width = icSz, Height = icSz, HorizontalAlignment = HorizontalAlignment.Center };
        iconCv.Children.Add(sun);
        iconCv.Children.Add(cloud1);
        iconCv.Children.Add(cloud2);
        stack.Children.Add(iconCv);
        // 温度文字：从 Plugin.Text 解析，默认 24°C
        string temp = "24°C";
        if (!string.IsNullOrWhiteSpace(p.Text)) temp = p.Text;
        var tempTb = new TextBlock
        {
            Text = temp,
            FontSize = size * 0.22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(tempTb);
        bg.Child = stack;
        return bg;
    }

    // ===== 插件 6：日历小方块（上方显示月份、下方显示日期数字） =====
    private FrameworkElement BuildCalendarTile(FolderPlugin p, double size)
    {
        ThemeHelper.TryParseColor(string.IsNullOrWhiteSpace(p.Color) ? "#D32F2F" : p.Color, out var accent);
        var card = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size * 0.1),
            Background = Brushes.White,
            Padding = new Thickness(0),
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25, Color = Colors.Black }
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(size * 0.28) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(size * 0.1, size * 0.1, 0, 0)
        };
        Grid.SetRow(top, 0);
        var month = new TextBlock
        {
            Text = DateTime.Now.ToString("MMM"),
            Foreground = Brushes.White,
            FontSize = size * 0.17,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        top.Child = month;
        grid.Children.Add(top);

        var day = new TextBlock
        {
            Text = DateTime.Now.Day.ToString(),
            FontSize = size * 0.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(day, 1);
        grid.Children.Add(day);
        card.Child = grid;

        // 每天 0 点刷新一次数字（10 分钟轮询足够）
        void tick(object? s, EventArgs e)
        {
            month.Text = DateTime.Now.ToString("MMM");
            day.Text = DateTime.Now.Day.ToString();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timer.Tick += tick;
        timer.Start();
        _pluginTimers.Add(timer);

        return card;
    }

    // ===== 插件 7：音乐播放器 =====
    private FrameworkElement BuildMusicPlayerCollapsed(FolderPlugin p, double size)
    {
        // 音乐播放器在折叠态下占据整个文件夹（size - 4px margin）
        double w = size;
        double h = size;
        double pad = 2; // 4px total margin (2px each side)

        var root = new Border
        {
            Width = w - pad * 2,
            Height = h - pad * 2,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Margin = new Thickness(pad),
            ClipToBounds = true,
            IsHitTestVisible = true
        };

        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 顶部：歌曲信息
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(size * 0.18) }); // 底部：控制按钮

        // === 顶部区域 ===
        var topArea = new Grid();
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 专辑封面
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 歌曲信息
        topArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 上箭头按钮

        // 专辑封面（左侧）
        double albumSize = Math.Min(w - pad * 2, h - pad * 2) * 0.5;
        var albumArt = new Border
        {
            Width = albumSize,
            Height = albumSize,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0x44, 0x44, 0x44)),
            Margin = new Thickness(6, 6, 4, 4),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _musicAlbumArt = albumArt;

        // 专辑封面默认内容（彩色圆形+K字代表酷狗）
        var albumContent = new Grid();
        var albumBg = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(0x4A, 0x90, 0xE2),
                Color.FromRgb(0x1A, 0x23, 0x7E),
                new Point(0, 0), new Point(1, 1)),
            CornerRadius = new CornerRadius(8)
        };
        albumContent.Children.Add(albumBg);

        var kugouLogo = new TextBlock
        {
            Text = "K",
            Foreground = Brushes.White,
            FontSize = albumSize * 0.35,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        albumContent.Children.Add(kugouLogo);
        albumArt.Child = albumContent;
        _musicAlbumArtContent = albumContent;

        // 点击专辑封面打开酷狗
        albumArt.MouseLeftButtonDown += (_, _) =>
        {
            _musicService?.OpenKugou();
        };

        Grid.SetColumn(albumArt, 0);
        topArea.Children.Add(albumArt);

        // 歌曲信息（中间）
        var infoPanel = new StackPanel
        {
            Margin = new Thickness(6, 8, 4, 4),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 歌曲名（流动显示）
        var titleTb = new TextBlock
        {
            Text = "未检测到音乐",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            FontSize = Math.Max(10, w * 0.06),
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(10, w - pad * 2 - albumSize - 40)
        };
        _musicTitleMarquee = titleTb;
        infoPanel.Children.Add(titleTb);

        // 艺术家
        var artistTb = new TextBlock
        {
            Text = "未在播放音乐",
            Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
            FontSize = Math.Max(8, w * 0.045),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(10, w - pad * 2 - albumSize - 40)
        };
        _musicArtistText = artistTb;
        infoPanel.Children.Add(artistTb);

        Grid.SetColumn(infoPanel, 1);
        topArea.Children.Add(infoPanel);

        // 上箭头按钮（右上角）
        double btnSize = Math.Max(16, w * 0.1);
        var expandBtn = new Button
        {
            Width = btnSize,
            Height = btnSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "展开播放器"
        };
        var expandPath = new System.Windows.Shapes.Path
        {
            Data = GetOrCreateGeom("uparrow", btnSize, CreateUpArrowPath),
            Fill = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF))
        };
        expandBtn.Content = expandPath;
        expandBtn.Click += (_, _) =>
        {
            // 切换展开状态
            if (_expanded)
                Collapse();
            else
                Expand();
        };
        Grid.SetColumn(expandBtn, 2);
        expandBtn.HorizontalAlignment = HorizontalAlignment.Right;
        expandBtn.Margin = new Thickness(0, 4, 6, 0);
        topArea.Children.Add(expandBtn);

        Grid.SetRow(topArea, 0);
        outerGrid.Children.Add(topArea);

        // === 底部控制栏 ===
        var controls = new Grid
        {
            Margin = new Thickness(8, 0, 8, 6)
        };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 收藏
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 分隔
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 上一曲
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 播放/暂停
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 下一曲
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 分隔

        double ctrlBtnSize = Math.Max(18, Math.Min(w * 0.1, h * 0.12));
        var ctrlForeground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));

        // 注：收藏按钮无对应媒体键/酷狗接口（点击无响应，死按钮），已隐藏；待后续有实现方案再加回。

        // 上一曲按钮
        var prevBtn = new Button
        {
            Width = ctrlBtnSize,
            Height = ctrlBtnSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "上一曲"
        };
        prevBtn.Content = new System.Windows.Shapes.Path
        {
            Data = GetOrCreateGeom("prev", ctrlBtnSize, CreatePrevPath),
            Fill = ctrlForeground
        };
        prevBtn.Click += (_, _) => _musicService?.PrevTrack();
        _musicPrevBtn = prevBtn;
        Grid.SetColumn(prevBtn, 2);
        controls.Children.Add(prevBtn);

        // 播放/暂停按钮（中间最大）
        double playSize = ctrlBtnSize * 1.2;
        var playBtn = new Button
        {
            Width = playSize,
            Height = playSize,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "播放/暂停",
            Margin = new Thickness(4, 0, 4, 0)
        };
        playBtn.Content = new System.Windows.Shapes.Path
        {
            Data = GetOrCreateGeom("play", playSize, CreatePlayPath),
            Fill = ctrlForeground
        };
        playBtn.Click += (_, _) => _musicService?.PlayPause();
        _musicPlayPauseBtn = playBtn;
        Grid.SetColumn(playBtn, 3);
        controls.Children.Add(playBtn);

        // 下一曲按钮
        var nextBtn = new Button
        {
            Width = ctrlBtnSize,
            Height = ctrlBtnSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "下一曲"
        };
        nextBtn.Content = new System.Windows.Shapes.Path
        {
            Data = GetOrCreateGeom("next", ctrlBtnSize, CreateNextPath),
            Fill = ctrlForeground
        };
        nextBtn.Click += (_, _) => _musicService?.NextTrack();
        _musicNextBtn = nextBtn;
        Grid.SetColumn(nextBtn, 4);
        controls.Children.Add(nextBtn);

        Grid.SetRow(controls, 1);
        outerGrid.Children.Add(controls);

        root.Child = outerGrid;
        _musicPlayerCollapsed = root;
        return root;
    }

    /// <summary>获取静态图标（优先走缓存，PathGeometry+Freeze）</summary>
    private static Geometry GetOrCreateGeom(string key, double size, Func<double, Geometry> factory)
    {
        // 量化到最近的2像素，避免缓存条目爆炸
        int qs = (int)Math.Round(size / 2.0) * 2;
        string fullKey = $"{key}_{qs}";
        if (_geomCache.TryGetValue(fullKey, out var cached)) return cached;
        var g = factory(qs);
        try { if (g.CanFreeze) g.Freeze(); } catch { /* ignore */ }
        _geomCache[fullKey] = g;
        return g;
    }

    /// <summary>创建上箭头路径（PathGeometry，无 ByteStream 风险）</summary>
    private static Geometry CreateUpArrowPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(s * 0.2, s * 0.5), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(s * 0.5, s * 0.2), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.8, s * 0.5), true));
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>创建心形路径</summary>
    private static Geometry CreateHeartPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(s * 0.5, s * 0.85), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(s * 0.15, s * 0.5), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.15, s * 0.3), true));
        fig.Segments.Add(new ArcSegment(new Point(s * 0.35, s * 0.2), new Size(s * 0.2, s * 0.2), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new ArcSegment(new Point(s * 0.5, s * 0.35), new Size(s * 0.2, s * 0.2), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new ArcSegment(new Point(s * 0.65, s * 0.2), new Size(s * 0.2, s * 0.2), 0, false, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.85, s * 0.3), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.85, s * 0.5), true));
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>创建上一曲路径</summary>
    private static Geometry CreatePrevPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig1 = new PathFigure { StartPoint = new Point(s * 0.7, s * 0.2), IsClosed = true, IsFilled = true };
        fig1.Segments.Add(new LineSegment(new Point(s * 0.7, s * 0.8), true));
        fig1.Segments.Add(new LineSegment(new Point(s * 0.3, s * 0.5), true));
        g.Figures.Add(fig1);
        var fig2 = new PathFigure { StartPoint = new Point(s * 0.5, s * 0.2), IsClosed = true, IsFilled = true };
        fig2.Segments.Add(new LineSegment(new Point(s * 0.5, s * 0.8), true));
        fig2.Segments.Add(new LineSegment(new Point(s * 0.1, s * 0.5), true));
        g.Figures.Add(fig2);
        return g;
    }

    /// <summary>创建播放路径</summary>
    private static Geometry CreatePlayPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(s * 0.35, s * 0.2), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(s * 0.35, s * 0.8), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.75, s * 0.5), true));
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>创建暂停路径</summary>
    private static Geometry CreatePausePath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig1 = new PathFigure { StartPoint = new Point(s * 0.3, s * 0.2), IsClosed = true, IsFilled = true };
        fig1.Segments.Add(new LineSegment(new Point(s * 0.45, s * 0.2), true));
        fig1.Segments.Add(new LineSegment(new Point(s * 0.45, s * 0.8), true));
        fig1.Segments.Add(new LineSegment(new Point(s * 0.3, s * 0.8), true));
        g.Figures.Add(fig1);
        var fig2 = new PathFigure { StartPoint = new Point(s * 0.55, s * 0.2), IsClosed = true, IsFilled = true };
        fig2.Segments.Add(new LineSegment(new Point(s * 0.7, s * 0.2), true));
        fig2.Segments.Add(new LineSegment(new Point(s * 0.7, s * 0.8), true));
        fig2.Segments.Add(new LineSegment(new Point(s * 0.55, s * 0.8), true));
        g.Figures.Add(fig2);
        return g;
    }

    /// <summary>创建下一曲路径</summary>
    private static Geometry CreateNextPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig1 = new PathFigure { StartPoint = new Point(s * 0.3, s * 0.2), IsClosed = true, IsFilled = true };
        fig1.Segments.Add(new LineSegment(new Point(s * 0.3, s * 0.8), true));
        fig1.Segments.Add(new LineSegment(new Point(s * 0.7, s * 0.5), true));
        g.Figures.Add(fig1);
        var fig2 = new PathFigure { StartPoint = new Point(s * 0.5, s * 0.2), IsClosed = true, IsFilled = true };
        fig2.Segments.Add(new LineSegment(new Point(s * 0.5, s * 0.8), true));
        fig2.Segments.Add(new LineSegment(new Point(s * 0.9, s * 0.5), true));
        g.Figures.Add(fig2);
        return g;
    }

    /// <summary>创建固定（图钉）图标路径</summary>
    private static Geometry CreatePinPath(double size)
    {
        double s = size;
        var g = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(s * 0.5, s * 0.1), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(new Point(s * 0.7, s * 0.3), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.6, s * 0.35), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.6, s * 0.7), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.4, s * 0.9), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.35, s * 0.65), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.25, s * 0.6), true));
        fig.Segments.Add(new LineSegment(new Point(s * 0.3, s * 0.3), true));
        g.Figures.Add(fig);
        return g;
    }

    // ===== 音乐播放器：展开态UI =====

    /// <summary>
    /// 构建展开态音乐播放器UI（作为Panel的子元素覆盖显示）
    /// </summary>
    private Border BuildMusicPlayerExpanded(double width, double height)
    {
        var root = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            ClipToBounds = true
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 顶部歌曲信息
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 中间歌词区
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 底部控制栏

        // === 顶部：歌曲信息 + 固定按钮 + 收起按钮 ===
        var topBar = new Grid
        {
            Margin = new Thickness(12, 8, 8, 4)
        };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 专辑
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 歌名+艺术家
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 固定按钮
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 收起按钮

        double expandedAlbumSize = Math.Min(width * 0.2, height * 0.25);
        var expAlbumArt = new Border
        {
            Width = expandedAlbumSize,
            Height = expandedAlbumSize,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x60, 0x44, 0x44, 0x44)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0)
        };
        var expAlbumContent = new Grid();
        var expAlbumBg = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(0x4A, 0x90, 0xE2),
                Color.FromRgb(0x1A, 0x23, 0x7E),
                new Point(0, 0), new Point(1, 1)),
            CornerRadius = new CornerRadius(10)
        };
        expAlbumContent.Children.Add(expAlbumBg);
        var expKLogo = new TextBlock
        {
            Text = "K",
            Foreground = Brushes.White,
            FontSize = expandedAlbumSize * 0.35,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        expAlbumContent.Children.Add(expKLogo);
        expAlbumArt.Child = expAlbumContent;
        _musicExpandedAlbumArt = expAlbumArt;
        _musicExpandedAlbumArtContent = expAlbumContent;
        expAlbumArt.MouseLeftButtonDown += (_, _) => _musicService?.OpenKugou();
        Grid.SetColumn(expAlbumArt, 0);
        topBar.Children.Add(expAlbumArt);

        // 歌曲名 + 艺术家
        var titlePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var expTitle = new TextBlock
        {
            Text = "未检测到音乐",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            FontSize = Math.Max(12, width * 0.04),
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = width * 0.5
        };
        _musicExpandedTitle = expTitle;
        titlePanel.Children.Add(expTitle);

        var expArtist = new TextBlock
        {
            Text = "未在播放音乐",
            Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
            FontSize = Math.Max(10, width * 0.03),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _musicExpandedArtist = expArtist;
        titlePanel.Children.Add(expArtist);
        Grid.SetColumn(titlePanel, 1);
        topBar.Children.Add(titlePanel);

        // 固定按钮（右上角）
        double pinBtnSize = Math.Max(20, width * 0.04);
        var pinBtn = new Button
        {
            Width = pinBtnSize,
            Height = pinBtnSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "固定展开状态",
            Margin = new Thickness(4, 0, 4, 0)
        };
        var pinPath = new System.Windows.Shapes.Path
        {
            Data = GetOrCreateGeom("pin", pinBtnSize, CreatePinPath),
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
        };
        pinBtn.Content = pinPath;
        pinBtn.Click += (_, _) =>
        {
            _musicPinned = !_musicPinned;
            pinPath.Fill = _musicPinned
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00))
                : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            pinBtn.ToolTip = _musicPinned ? "取消固定" : "固定展开状态";
        };
        _musicPinBtn = pinBtn;
        Grid.SetColumn(pinBtn, 2);
        topBar.Children.Add(pinBtn);

        // 收起按钮
        double collapseBtnSize = Math.Max(20, width * 0.04);
        var collapseBtn = new Button
        {
            Width = collapseBtnSize,
            Height = collapseBtnSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "收起",
            Content = new TextBlock
            {
                Text = "×",
                Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
                FontSize = collapseBtnSize * 0.8,
                FontWeight = FontWeights.Bold
            }
        };
        collapseBtn.Click += (_, _) =>
        {
            _musicPinned = false;
            Collapse();
        };
        Grid.SetColumn(collapseBtn, 3);
        topBar.Children.Add(collapseBtn);

        Grid.SetRow(topBar, 0);
        grid.Children.Add(topBar);

        // === 中间：歌词显示区（纯歌词，无播放列表） ===
        var lyricsArea = new Grid
        {
            Margin = new Thickness(16, 4, 16, 4)
        };

        // 歌词滚动容器
        var lyricsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            IsHitTestVisible = false,
            Focusable = false
        };

        // 使用 OpacityMask 实现歌词上下边缘虚化
        var fadeBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.0),
                new GradientStop(Color.FromArgb(0xFF, 0x00, 0x00, 0x00), 0.15),
                new GradientStop(Color.FromArgb(0xFF, 0x00, 0x00, 0x00), 0.85),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0)
            }
        };
        lyricsScroll.OpacityMask = fadeBrush;

        // 歌词面板（动态填充 TextBlock）
        var lyricsPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 默认占位
        var placeholder = new TextBlock
        {
            Text = "暂无歌词",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x90, 0xE2)),
            FontSize = Math.Max(12, width * 0.03),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 20, 0, 20)
        };
        lyricsPanel.Children.Add(placeholder);

        lyricsScroll.Content = lyricsPanel;
        lyricsArea.Children.Add(lyricsScroll);

        _musicLyricsScroll = lyricsScroll;
        _musicLyricsPanel = lyricsPanel;

        Grid.SetRow(lyricsArea, 1);
        grid.Children.Add(lyricsArea);

        // === 底部控制栏（与折叠态相同）===
        var expControls = new Grid
        {
            Margin = new Thickness(12, 4, 12, 10)
        };
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expControls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        double expCtrlSize = Math.Max(22, Math.Min(width * 0.05, height * 0.07));
        var ctrlFg = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));

        // 注：收藏按钮无对应媒体键/酷狗接口（点击无响应，死按钮），已隐藏；待后续有实现方案再加回。

        // 上一曲
        var expPrev = new Button
        {
            Width = expCtrlSize,
            Height = expCtrlSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = new System.Windows.Shapes.Path { Data = GetOrCreateGeom("prev", expCtrlSize, CreatePrevPath), Fill = ctrlFg }
        };
        expPrev.Click += (_, _) => _musicService?.PrevTrack();
        _musicExpandedPrevBtn = expPrev;
        Grid.SetColumn(expPrev, 2);
        expControls.Children.Add(expPrev);

        // 播放/暂停
        double expPlaySize = expCtrlSize * 1.3;
        var expPlay = new Button
        {
            Width = expPlaySize,
            Height = expPlaySize,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(6, 0, 6, 0),
            Content = new System.Windows.Shapes.Path { Data = GetOrCreateGeom("play", expPlaySize, CreatePlayPath), Fill = ctrlFg }
        };
        expPlay.Click += (_, _) => _musicService?.PlayPause();
        _musicExpandedPlayPauseBtn = expPlay;
        Grid.SetColumn(expPlay, 3);
        expControls.Children.Add(expPlay);

        // 下一曲
        var expNext = new Button
        {
            Width = expCtrlSize,
            Height = expCtrlSize,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = new System.Windows.Shapes.Path { Data = GetOrCreateGeom("next", expCtrlSize, CreateNextPath), Fill = ctrlFg }
        };
        expNext.Click += (_, _) => _musicService?.NextTrack();
        _musicExpandedNextBtn = expNext;
        Grid.SetColumn(expNext, 4);
        expControls.Children.Add(expNext);

        Grid.SetRow(expControls, 2);
        grid.Children.Add(expControls);

        root.Child = grid;
        _musicPlayerExpanded = root;
        return root;
    }

    /// <summary>
    /// 更新音乐播放器UI（歌曲信息变化时调用）
    /// </summary>
    private void UpdateMusicPlayerUI()
    {
        if (_musicService == null || _musicPlayerPlugin == null) return;

        try
        {
            string title = _musicService.CurrentTitle;
            string artist = _musicService.CurrentArtist;

            string titleDisplay = string.IsNullOrEmpty(title) ? "未检测到音乐" : title;
            string artistDisplay = string.IsNullOrEmpty(artist) ? "未在播放音乐" : artist;

            // 更新折叠态UI（仅在值实际变化时更新，避免无意义重绘）
            if (_musicTitleMarquee != null && _musicTitleMarquee.Text != titleDisplay)
                _musicTitleMarquee.Text = titleDisplay;
            if (_musicArtistText != null && _musicArtistText.Text != artistDisplay)
                _musicArtistText.Text = artistDisplay;

            // 更新展开态UI（如果存在且值变化）
            if (_musicExpandedTitle != null && _musicExpandedTitle.Text != titleDisplay)
                _musicExpandedTitle.Text = titleDisplay;
            if (_musicExpandedArtist != null && _musicExpandedArtist.Text != artistDisplay)
                _musicExpandedArtist.Text = artistDisplay;

            // 重建歌词面板
            RebuildLyricsPanel();

            // 更新播放/暂停图标
            UpdatePlayPauseIcon(_musicPlayPauseBtn);
            UpdatePlayPauseIcon(_musicExpandedPlayPauseBtn);

            // 更新歌词高亮和滚动位置
            UpdateLyricsPosition();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] UpdateMusicPlayerUI error: {ex.Message}");
        }
    }

    /// <summary>重建歌词面板（歌词数据变化时调用）</summary>
    private void RebuildLyricsPanel()
    {
        if (_musicLyricsPanel == null || _musicService == null) return;

        // 面板重建后旧滚动位置已无意义：停掉在途滚动动画，下次滚动直接落位
        _lyricsAnimActive = false;
        _lyricsSnapNext = true;
        _lastLyricIndex = -1;

        try
        {
            _musicLyricLineElements.Clear();
            _musicLyricsPanel.Children.Clear();

            var lyrics = _musicService.CurrentLyrics;
            if (lyrics.Count == 0)
            {
                // 显示占位文本（正在加载或无歌词）
                var placeholder = new TextBlock
                {
                    Text = string.IsNullOrEmpty(_musicService.CurrentTitle) ? "未检测到音乐" : "歌词加载中...",
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x90, 0xE2)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 20)
                };
                _musicLyricsPanel.Children.Add(placeholder);
                return;
            }

            // 字体大小：用户设置 > 自动按宽度缩放
            double baseFontSize = 14;
            if (_musicPlayerPlugin != null && _musicPlayerPlugin.LyricFontSize > 0)
                baseFontSize = _musicPlayerPlugin.LyricFontSize;
            else if (_musicPlayerExpanded != null)
                baseFontSize = Math.Max(12, _musicPlayerExpanded.ActualWidth * 0.03);

            // 歌词最大宽度：限制不超出文件夹框
            double maxTextWidth = (_musicPlayerExpanded?.ActualWidth ?? 300) * 0.85;

            // 顶部留白：让第一句歌词能居中显示
            double scrollHeight = _musicLyricsScroll?.ActualHeight > 0 ? _musicLyricsScroll.ActualHeight : 200;
            var topSpacer = new FrameworkElement
            {
                Height = scrollHeight / 2 - baseFontSize,
                Visibility = Visibility.Visible
            };
            _musicLyricsPanel.Children.Add(topSpacer);

            foreach (var line in lyrics)
            {
                var linePanel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 3)
                };

                var textBlock = new TextBlock
                {
                    Text = line.Text,
                    FontSize = baseFontSize,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontWeight = FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = maxTextWidth
                };
                linePanel.Children.Add(textBlock);

                // 翻译行
                if (line.HasTranslation)
                {
                    var transBlock = new TextBlock
                    {
                        Text = line.Translation,
                        FontSize = baseFontSize * 0.8,
                        Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = maxTextWidth * 0.9
                    };
                    linePanel.Children.Add(transBlock);
                }

                _musicLyricsPanel.Children.Add(linePanel);
                _musicLyricLineElements.Add(textBlock);
            }

            // 底部留白：让最后一句歌词能居中显示
            var bottomSpacer = new FrameworkElement
            {
                Height = scrollHeight / 2 - baseFontSize,
                Visibility = Visibility.Visible
            };
            _musicLyricsPanel.Children.Add(bottomSpacer);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] RebuildLyricsPanel error: {ex.Message}");
        }
    }

    /// <summary>更新歌词高亮和滚动位置（歌词进度变化时调用）</summary>
    private void UpdateLyricsPosition()
    {
        if (_musicLyricsScroll == null || _musicService == null) return;

        try
        {
            int currentIndex = _musicService.CurrentLyricIndex;
            var lyrics = _musicService.CurrentLyrics;

            // 更新所有行的高亮状态
            for (int i = 0; i < _musicLyricLineElements.Count; i++)
            {
                var tb = _musicLyricLineElements[i];
                if (i == currentIndex)
                {
                    // 当前行：酷狗蓝 + 加粗
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x90, 0xE2));
                    tb.FontWeight = FontWeights.Bold;
                }
                else
                {
                    // 非当前行：白色按距离渐暗
                    double distance = currentIndex >= 0 ? Math.Abs(i - currentIndex) : 0;
                    byte alpha = distance switch
                    {
                        0 => 0xFF,
                        1 => 0xCC,
                        2 => 0x99,
                        3 => 0x66,
                        _ => 0x40
                    };
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
                    tb.FontWeight = FontWeights.Normal;
                }
            }

            // 滚动到当前行（居中）
            if (currentIndex >= 0 && currentIndex < _musicLyricLineElements.Count && _musicLyricsScroll != null && _musicLyricsPanel != null)
            {
                var targetLine = _musicLyricLineElements[currentIndex];
                // 计算目标滚动偏移：让当前行居中
                double targetY = targetLine.TransformToAncestor(_musicLyricsPanel).Transform(new Point(0, 0)).Y;
                double scrollHeight = _musicLyricsScroll.ActualHeight;
                double panelHeight = _musicLyricsPanel.ActualHeight;
                double offset = targetY - scrollHeight / 2 + targetLine.ActualHeight / 2;
                // 限制在有效范围内
                double maxOffset = panelHeight - scrollHeight;
                offset = Math.Max(0, Math.Min(offset, maxOffset > 0 ? maxOffset : 0));

                // 瞬间落位场景：面板重建后首次 / 首次出现当前行（-1→0）/ 视口未就绪 / 大跨度跳动（如 seek 拖进度）
                double currentOffset = _musicLyricsScroll.VerticalOffset;
                bool snap = _lyricsSnapNext
                    || _lastLyricIndex < 0
                    || scrollHeight <= 0
                    || Math.Abs(offset - currentOffset) > scrollHeight * 1.5;
                _lyricsSnapNext = false;
                _lastLyricIndex = currentIndex;

                if (snap)
                {
                    _lyricsAnimActive = false;
                    _musicLyricsScroll.ScrollToVerticalOffset(offset);
                }
                else
                {
                    StartLyricsScrollAnimation(offset);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] UpdateLyricsPosition error: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新播放/暂停按钮图标
    /// </summary>
    private void UpdatePlayPauseIcon(Button? button)
    {
        if (button == null || _musicService == null) return;

        try
        {
            double btnSize = Math.Max(16, button.Width > 0 ? button.Width : 24);
            var path = _musicService.IsPlaying
                ? GetOrCreateGeom("pause", btnSize, CreatePausePath)
                : GetOrCreateGeom("play", btnSize, CreatePlayPath);
            var fillBrush = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF));

            if (button.Content is System.Windows.Shapes.Path existingPath)
            {
                existingPath.Data = path;
                existingPath.Fill = fillBrush;
            }
            else
            {
                button.Content = new System.Windows.Shapes.Path
                {
                    Data = path,
                    Fill = fillBrush
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] UpdatePlayPauseIcon error: {ex.Message}");
        }
    }

    /// <summary>更新专辑封面显示（折叠态 + 展开态）。有真实封面→ImageBrush 填充并隐藏 K logo；无→恢复占位。</summary>
    private void UpdateAlbumArtUI()
    {
        if (_musicService == null) return;
        try
        {
            var art = _musicService.CurrentAlbumArt;
            ApplyAlbumArt(_musicAlbumArt, _musicAlbumArtContent, art);
            ApplyAlbumArt(_musicExpandedAlbumArt, _musicExpandedAlbumArtContent, art);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] UpdateAlbumArtUI error: {ex.Message}");
        }
    }

    /// <summary>把封面图应用到某个封面 Border（有图用 ImageBrush 填充并隐藏占位内容，无图恢复半透明灰占位）。</summary>
    private static void ApplyAlbumArt(Border? artBorder, UIElement? placeholder, BitmapSource? art)
    {
        if (artBorder == null) return;
        if (art != null)
        {
            artBorder.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            artBorder.Background = new SolidColorBrush(Color.FromArgb(0x60, 0x44, 0x44, 0x44));
            if (placeholder != null) placeholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>初始化音乐播放器服务（当文件夹包含 MusicPlayer 插件时调用）</summary>
    private void InitMusicService()
    {
        if (_musicService != null) return;

        // 首个音乐插件文件夹初始化时惰性启动全局音乐服务（无音乐文件夹时 App 启动阶段不会启动它）
        App.EnsureMusicStarted();

        // 订阅全局共享单例（App.Music），用命名方法以便 Cleanup 时正确退订
        _musicService = App.Music;
        _musicService.SongInfoChanged += OnMusicSongInfoChanged;
        // 播放状态变化：只更新图标，不重建歌词面板（避免丢失滚动位置）
        _musicService.PlaybackStateChanged += OnMusicPlaybackStateChanged;
        _musicService.LyricsChanged += OnMusicSongInfoChanged; // 歌词数据变化 → 走完整 UI 刷新（同歌曲信息）
        // 歌词位置变化：仅更新高亮和滚动，不重建面板（高频事件，轻量处理）
        _musicService.LyricsPositionChanged += OnMusicLyricsPositionChanged;
        // 专辑封面变化：更新折叠/展开态封面
        _musicService.AlbumArtChanged += OnMusicAlbumArtChanged;
        // 单例由 App 统一 Start/Stop，这里只订阅；并用当前状态立即刷新一次（单例可能已在播放）
        UpdateMusicPlayerUI();
        UpdatePlayPauseIconsOnly();
        UpdateAlbumArtUI();
    }

    // 命名事件处理器（lambda 无法退订；单例共享下必须可退订，否则窗口关闭后仍被 App.Music 持有导致泄漏）
    private void OnMusicSongInfoChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateMusicPlayerUI));
    private void OnMusicPlaybackStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(UpdatePlayPauseIconsOnly));
    private void OnMusicLyricsPositionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateLyricsPosition));
    private void OnMusicAlbumArtChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateAlbumArtUI));

    /// <summary>仅更新播放/暂停图标（不触发歌词面板重建）</summary>
    private void UpdatePlayPauseIconsOnly()
    {
        try
        {
            UpdatePlayPauseIcon(_musicPlayPauseBtn);
            UpdatePlayPauseIcon(_musicExpandedPlayPauseBtn);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DeskFolder] UpdatePlayPauseIconsOnly error: {ex.Message}");
        }
    }

    /// <summary>清理音乐播放器服务</summary>
    private void CleanupMusicService()
    {
        if (_musicService != null)
        {
            // 只退订共享单例的事件，不 Stop/Dispose（App.Music 由 App 统一管理生命周期）
            _musicService.SongInfoChanged -= OnMusicSongInfoChanged;
            _musicService.PlaybackStateChanged -= OnMusicPlaybackStateChanged;
            _musicService.LyricsChanged -= OnMusicSongInfoChanged;
            _musicService.LyricsPositionChanged -= OnMusicLyricsPositionChanged;
            _musicService.AlbumArtChanged -= OnMusicAlbumArtChanged;
            _musicService = null;
        }
        _musicPlayerPlugin = null;
        _musicPlayerCollapsed = null;
        _musicPlayerExpanded = null;
        _musicAlbumArtContent = null;
        _musicExpandedAlbumArt = null;
        _musicExpandedAlbumArtContent = null;
        _musicPinned = false;
        _musicExpandedTitle = null;
        _musicExpandedArtist = null;
        _musicExpandedPlayPauseBtn = null;
        _musicExpandedPrevBtn = null;
        _musicExpandedNextBtn = null;
        _musicPinBtn = null;
        _musicLyricsScroll = null;
        _musicLyricsPanel = null;
        _musicLyricLineElements.Clear();
        // 停掉歌词滚动动画并复位状态；若展开/收起动画未在跑则同时退订帧回调，防泄漏
        _lyricsAnimActive = false;
        _lyricsSnapNext = false;
        _lastLyricIndex = -1;
        if (!_animating)
            CompositionTarget.Rendering -= OnRenderFrame;
    }
    private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
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
        /// <summary>承载 Img/Media 的容器：ClipToBounds 裁掉视频放大/裁剪变换的溢出，避免影响相邻元素。</summary>
        public Grid Host = null!;
        /// <summary>视频背景元素（按需创建，图片为主题时保持 null）。静音循环播放。</summary>
        public MediaElement? Media = null;
        /// <summary>当前已加载的视频路径。仅当路径变化时才重新设置 Source，
        /// 避免每次 ReloadSlot/轮播都重建 MediaElement 的内部播放管线（否则旧的会泄漏非托管缓冲）。</summary>
        public string? VideoPath = null;
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
        // 仅让当前可见的槽播放视频，隐藏槽暂停解码（文件夹初始为折叠态）
        SyncVideoPlayback(_expanded);
    }

    /// <summary>在 Border 内部网格中插入一个铺满的图片/视频容器，返回状态槽。
    /// 容器 Host（Grid）开启 ClipToBounds：视频裁剪用 RenderTransform 变换时溢出部分会被裁掉，
    /// 与图片 CroppedBitmap 的视觉效果一致（选框区域精确铺满、不外溢）。</summary>
    private ImageSlot? CreateSlot(Border target, ImagePlaylist playlist, bool useExpandedCrop, ThemeConfig theme)
    {
        if (target.Child is not Grid grid) return null;
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        var img = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = ThemeHelper.Clamp(theme.BackgroundOpacity, 0, 1),
            IsHitTestVisible = false
        };
        host.Children.Add(img);
        int rows = grid.RowDefinitions.Count > 0 ? grid.RowDefinitions.Count : 1;
        int cols = grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1;
        Grid.SetRowSpan(host, rows);
        Grid.SetColumnSpan(host, cols);
        grid.Children.Insert(0, host);
        _themeVisuals.Add(host);
        target.Background = System.Windows.Media.Brushes.Transparent;
        return new ImageSlot { Target = target, Playlist = playlist, UseExpandedCrop = useExpandedCrop, Theme = theme, Img = img, Host = host };
    }

    /// <summary>读取某状态（折叠/展开）的有效裁剪区域（归一化 0-1，相对原图）。优先文件夹级，否则主题级。
    /// 返回 false 表示无裁剪（显示整图）。供图片 CropFrame 与视频 ApplyVideoCrop 共用。</summary>
    private bool TryReadCrop(bool expanded, ThemeConfig theme,
        out double nx, out double ny, out double nw, out double nh)
    {
        nx = ny = nw = nh = 0;
        bool hasCrop = (_config.HasFolderImageCrop || _config.HasFolderImageCropExpanded)
            ? (expanded ? _config.HasFolderImageCropExpanded : _config.HasFolderImageCrop)
            : (expanded ? theme.HasImageCropExpanded : theme.HasImageCrop);
        if (!hasCrop) return false;

        if (expanded ? _config.HasFolderImageCropExpanded : _config.HasFolderImageCrop)
        {
            nx = (expanded ? _config.FolderImageCropExpandedX : _config.FolderImageCropX)!.Value;
            ny = (expanded ? _config.FolderImageCropExpandedY : _config.FolderImageCropY)!.Value;
            nw = (expanded ? _config.FolderImageCropExpandedW : _config.FolderImageCropW)!.Value;
            nh = (expanded ? _config.FolderImageCropExpandedH : _config.FolderImageCropH)!.Value;
        }
        else
        {
            nx = (expanded ? theme.ImageCropExpandedX : theme.ImageCropX)!.Value;
            ny = (expanded ? theme.ImageCropExpandedY : theme.ImageCropY)!.Value;
            nw = (expanded ? theme.ImageCropExpandedW : theme.ImageCropW)!.Value;
            nh = (expanded ? theme.ImageCropExpandedH : theme.ImageCropH)!.Value;
        }
        return nw > 0 && nh > 0;
    }

    /// <summary>按裁剪区域对单帧取景；优先使用文件夹级裁剪配置（如果有），否则使用主题的裁剪配置；Stretch=Fill 保证选区精确铺满。</summary>
    private BitmapSource CropFrame(BitmapSource src, bool expanded, ThemeConfig theme)
    {
        if (!TryReadCrop(expanded, theme, out var nx, out var ny, out var nw, out var nh)) return src;

        int x = (int)Math.Round(nx * src.PixelWidth);
        int y = (int)Math.Round(ny * src.PixelHeight);
        int w = (int)Math.Round(nw * src.PixelWidth);
        int h = (int)Math.Round(nh * src.PixelHeight);
        x = Math.Max(0, Math.Min(x, src.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, src.PixelHeight - 1));
        w = Math.Max(1, Math.Min(w, src.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, src.PixelHeight - y));
        var cb = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
        // 源已冻结时一并冻结裁剪结果：跨线程安全 + 释放解码器引用，降低 GC 压力
        if (src.IsFrozen && !cb.IsFrozen) cb.Freeze();
        return cb;
    }

    /// <summary>计算某槽位图片解码的目标像素宽度（物理像素）：按显示尺寸 × DPI 降采样，
    /// 避免高分辨率原图（4K 等）全量解码后驻留内存。仅设宽度，高度按比例自动缩放。</summary>
    private int SlotDecodePx(ImageSlot slot)
    {
        double dpi = Math.Max(1.0, Math.Max(_dpiScaleX, _dpiScaleY));
        double logicalW, logicalH;
        if (slot.UseExpandedCrop)
        {
            // 展开态：优先用已计算的面板目标尺寸；未计算时按行列估算（与 RecomputeTargets 一致）
            logicalW = _panelTargetW > 0 ? _panelTargetW : EffectiveCols * IconCell + PanelPaddingH;
            logicalH = _panelTargetH > 0 ? _panelTargetH
                : Math.Max(EffectiveRows, 1) * IconCell + HeaderHeight + PanelPaddingV;
        }
        else
        {
            logicalW = CollapsedW;
            logicalH = CollapsedH;
        }
        // 取最长边对应的物理像素，作为 DecodePixelWidth 的上限（足够清晰且不过度占用内存）
        double maxLogical = Math.Max(logicalW, logicalH);
        return Math.Max(64, (int)Math.Ceiling(maxLogical * dpi));
    }

    /// <summary>重新加载某槽到指定索引的图片（含 GIF 逐帧动画接管）。裁剪随该状态（折叠/展开）各自生效。</summary>
    private void ReloadSlot(ImageSlot? slot, int index)
    {
        if (slot == null) return;
        if (slot.GifTimer != null) { _gifTimers.Remove(slot.GifTimer); slot.GifTimer.Stop(); slot.GifTimer = null; }
        var playlist = slot.Playlist;
        if (playlist.Paths.Count == 0)
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)); // 兜底淡底色
            StopVideo(slot);
            return;
        }
        index = ((index % playlist.Paths.Count) + playlist.Paths.Count) % playlist.Paths.Count;
        string path = playlist.Paths[index];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            StopVideo(slot);
            return;
        }
        try
        {
            if (ThemeHelper.IsVideoFile(path))
            {
                // 视频背景：静音循环播放，裁切由 ApplyVideoCrop 用 RenderTransform 实现
                LoadVideo(slot, path);
                slot.Target.Background = System.Windows.Media.Brushes.Transparent;
            }
            else if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                StopVideo(slot);
                // GIF 动图：需逐帧访问，用 BitmapDecoder（GIF 通常分辨率低，全量解码可接受）
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
            }
            else
            {
                StopVideo(slot);
                // 静态图（png/jpg/bmp/tif 等）：按显示尺寸降采样解码，避免高分辨率原图全量驻留内存
                int decodePx = SlotDecodePx(slot);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodePx;
                bmp.EndInit();
                bmp.Freeze(); // 跨线程安全 + 释放解码器引用
                slot.Img.Source = CropFrame(bmp, slot.UseExpandedCrop, slot.Theme);
            }
            slot.Target.Background = System.Windows.Media.Brushes.Transparent;
        }
        catch
        {
            slot.Target.Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            StopVideo(slot);
        }
    }

    /// <summary>在槽内播放视频背景（静音循环）。
    /// 采用 Source + Pause/Play 方案（而非 MediaClock）：
    /// 1) 避免 WPF MediaClock 在循环播放时持续分配非托管缓冲导致的「内存只涨不落」泄漏；
    /// 2) 仍保留「仅可见槽解码」优化——隐藏槽 Pause() 即停止解码，省 CPU/内存，且切换不闪；
    /// 3) 仅当视频路径变化时才重新设置 Source，避免每次 ReloadSlot/轮播都重建内部播放管线（旧管线泄漏）。
    /// 循环通过 MediaEnded 回到开头实现（边界处极轻微一帧跳变，远小于内存泄漏的危害）。</summary>
    private void LoadVideo(ImageSlot slot, string path)
    {
        if (slot.Media == null)
        {
            var me = new MediaElement
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                LoadedBehavior = MediaState.Manual,   // 手动控制 Play/Pause
                UnloadedBehavior = MediaState.Close,
                IsMuted = true,
                Volume = 0
            };
            // 播放结束自动回到开头循环（部分封装的 mp4 不会自动循环）
            me.MediaEnded += (_, _) =>
            {
                try { me.Position = TimeSpan.Zero; me.Play(); } catch { }
            };
            slot.Host.Children.Add(me);
            slot.Media = me;
            // 容器尺寸变化（展开/收起动画、窗口尺寸调整）时重新套用裁切变换
            slot.Host.SizeChanged += (_, _) => ApplyVideoCrop(slot);
        }
        // 路径未变则不再重建 Source（关键：防止重复创建内部播放管线导致非托管泄漏）
        if (!string.Equals(slot.VideoPath, path, StringComparison.OrdinalIgnoreCase))
        {
            try { slot.Media.Source = new Uri(path, UriKind.Absolute); } catch { }
            slot.VideoPath = path;
        }
        slot.Media.Visibility = Visibility.Visible;
        slot.Img.Visibility = Visibility.Collapsed;
        ApplyVideoCrop(slot);
    }

    /// <summary>停止并隐藏槽内的视频（切回图片/清空时调用）。</summary>
    private void StopVideo(ImageSlot slot)
    {
        if (slot.Media != null)
        {
            try { slot.Media.Pause(); } catch { }
            try { slot.Media.Source = null; } catch { }
            slot.VideoPath = null;
            slot.Media.Visibility = Visibility.Collapsed;
        }
        slot.Img.Visibility = Visibility.Visible;
    }

    /// <summary>按裁剪区域对视频施加 RenderTransform：选区（归一化）缩放 1/nw、1/nh 后平移到容器原点，
    /// 使选区精确铺满容器（与图片 CroppedBitmap + Stretch=Fill 效果一致）。无裁切则清空变换。</summary>
    private void ApplyVideoCrop(ImageSlot slot)
    {
        if (slot.Media == null) return;
        double W = slot.Host.ActualWidth, H = slot.Host.ActualHeight;
        if (W <= 0 || H <= 0) { slot.Media.RenderTransform = null; return; } // 布局未就绪，SizeChanged 会重算
        if (!TryReadCrop(slot.UseExpandedCrop, slot.Theme, out var nx, out var ny, out var nw, out var nh))
        {
            slot.Media.RenderTransform = null;
            return;
        }
        if (nw <= 0 || nh <= 0) { slot.Media.RenderTransform = null; return; }
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1 / nw, 1 / nh));
        tg.Children.Add(new TranslateTransform(-nx / nw * W, -ny / nh * H));
        slot.Media.RenderTransform = tg;
    }

    /// <summary>按当前可见状态同步视频播放：仅「可见槽」播放，隐藏槽暂停解码以省 CPU/内存。
    /// 一个文件夹非展开即折叠，同一时刻只有一个槽可见，故同一视频只解码一份（修复双解码导致的内存飙升与卡顿）。</summary>
    private void SyncVideoPlayback(bool expanded)
    {
        SyncOneVideo(_slotCollapsed, !expanded); // 折叠态：未展开时播放
        SyncOneVideo(_slotExpanded, expanded);    // 展开态：展开时播放
    }

    /// <summary>对单个槽：应播放则 Play（开始解码），应隐藏则 Pause（停止解码但不释放文件，切换回时不闪烁）。
    /// 无视频源时不作操作。</summary>
    private void SyncOneVideo(ImageSlot? slot, bool shouldPlay)
    {
        if (slot?.Media == null || slot.Media.Source == null) return;
        try
        {
            if (shouldPlay) slot.Media.Play();
            else slot.Media.Pause();
        }
        catch { }
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

    /// <summary>停止并清空图片轮播槽（含轮播计时器与视频播放），不触碰 GIF 计时器（由 ClearThemeVisuals 统一处理）。</summary>
    private void ClearImageSlots()
    {
        foreach (var t in _rotateTimers) t.Stop();
        _rotateTimers.Clear();
        // 同时停止 GIF 帧定时器（重载主题时旧 GIF 定时器必须停掉，否则会持续重复解码泄漏）
        foreach (var t in _gifTimers) { try { t.Stop(); } catch { } }
        _gifTimers.Clear();
        foreach (var slot in new[] { _slotCollapsed, _slotExpanded })
        {
            if (slot?.Media != null)
            {
                try { slot.Media.Stop(); } catch { }
                try { slot.Media.Source = null; } catch { }
                slot.VideoPath = null;
            }
        }
        _slotCollapsed = null;
        _slotExpanded = null;
    }

    /// <summary>窗口关闭时释放所有视频与轮播/GIF 计时器（避免 MediaElement / DispatcherTimer 泄漏）。</summary>
    private void StopAllVideos()
    {
        foreach (var slot in new[] { _slotCollapsed, _slotExpanded })
        {
            if (slot?.Media != null)
            {
                try { slot.Media.Stop(); } catch { }
                try { slot.Media.Source = null; } catch { }
                slot.VideoPath = null;
            }
        }
        foreach (var t in _rotateTimers) t.Stop();
        _rotateTimers.Clear();
        foreach (var t in _gifTimers) { try { t.Stop(); } catch { } }
        _gifTimers.Clear();
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

    // ===== 跨文件夹插件拖拽支持 =====

    /// <summary>窗口级拖拽经过：允许接收来自其他文件夹的插件，也允许同文件夹内拖拽</summary>
    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        // 检查是否是 DeskFolder 插件拖拽
        if (e.Data.GetDataPresent("DeskFolderPlugin"))
        {
            var data = e.Data.GetData("DeskFolderPlugin") as string;
            if (!string.IsNullOrEmpty(data))
            {
                var parts = data.Split(':');
                if (parts.Length == 2)
                {
                    // 无论来自其他文件夹还是本文件夹，都允许拖拽
                    e.Effects = System.Windows.DragDropEffects.Move;
                    e.Handled = true;
                    return;
                }
            }
        }
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>窗口级放置：处理跨文件夹插件移动，同文件夹拖拽转发给 IconGrid_Drop</summary>
    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DeskFolderPlugin")) return;

        var data = e.Data.GetData("DeskFolderPlugin") as string;
        if (string.IsNullOrEmpty(data)) return;

        // 解析数据：format = "sourceFolderId:pluginGridId"
        var parts = data.Split(':');
        if (parts.Length != 2) return;

        string sourceFolderId = parts[0];
        string pluginGridId = parts[1];

        // 同文件夹拖拽：转发给 IconGrid_Drop 处理
        if (sourceFolderId == _config.Id)
        {
            IconGrid_Drop(IconGrid, e);
            return;
        }

        // 查找源文件夹配置
        var sourceConfig = S.Data.Folders.FirstOrDefault(f => f.Id == sourceFolderId);
        if (sourceConfig == null) return;

        // 查找插件
        var plugin = sourceConfig.Plugins?.FirstOrDefault(p => p.GridId == pluginGridId);
        if (plugin == null) return;

        // 从源文件夹移除插件
        sourceConfig.Plugins?.Remove(plugin);

        // 添加到目标文件夹（重置位置）
        if (_config.Plugins == null)
            _config.Plugins = new List<FolderPlugin>();

        plugin.GridRow = -1;
        plugin.GridColumn = -1;
        _config.Plugins.Add(plugin);

        // 保存配置
        S.Save();

        // 刷新源文件夹窗口
        RefreshFolderWindow(sourceFolderId);

        // 刷新当前文件夹
        ApplyPlugins();
        if (_expanded)
        {
            BuildGrid();
            RecomputeTargets();
            Panel.Width = _panelTargetW;
            Panel.Height = _panelTargetH;
            Width = AnimWindowW();
            Height = AnimWindowH();
        }

        e.Handled = true;
    }

    /// <summary>刷新指定文件夹的窗口</summary>
    private static void RefreshFolderWindow(string folderId)
    {
        foreach (System.Windows.Window win in Application.Current.Windows)
        {
            if (win is FolderWindow fw && fw.Config.Id == folderId)
            {
                fw.Dispatcher.Invoke(() =>
                {
                    fw.ApplyPlugins();
                    if (fw._expanded)
                    {
                        fw.BuildGrid();
                        fw.RecomputeTargets();
                        fw.Panel.Width = fw._panelTargetW;
                        fw.Panel.Height = fw._panelTargetH;
                        fw.Width = fw.AnimWindowW();
                        fw.Height = fw.AnimWindowH();
                    }
                });
                break;
            }
        }
    }
}