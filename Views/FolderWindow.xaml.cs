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

        Loaded += OnLoaded;
        Closed += (_, _) => StopGifTimers(); // 窗口关闭时释放 GIF 计时器，避免泄漏
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

    /// <summary>展开面板中的图标+插件混合网格</summary>
    private void BuildGrid()
    {
        int cols = Math.Max(1, EffectiveCols);
        int rows = Math.Max(EffectiveRows, 5); // 至少5行以容纳插件

        // 收集所有插件（展开态显示的）
        var expandedPlugins = _config.Plugins?
            .Where(p => p.ShowOnExpanded && p.Type != FolderPluginType.None)
            .ToList() ?? new List<FolderPlugin>();

        // 计算所需的行数（考虑所有插件和图标的位置）
        int requiredRows = EffectiveRows;
        foreach (var p in expandedPlugins)
        {
            int neededRow = (p.GridRow >= 0 ? p.GridRow : 0) + p.GridRowSpan;
            requiredRows = Math.Max(requiredRows, neededRow);
        }
        if (_config.ShortcutPositions != null)
            foreach (var cell in _config.ShortcutPositions.Values)
            {
                int neededRow = (cell / cols) + 1;
                requiredRows = Math.Max(requiredRows, neededRow);
            }
        rows = Math.Max(rows, requiredRows);
        rows = Math.Max(rows, 5);

        // 设置行列定义
        IconGrid.RowDefinitions.Clear();
        IconGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < rows; i++)
            IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
        for (int i = 0; i < cols; i++)
            IconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconCell) });

        IconGrid.Children.Clear();

        // 构建占位矩阵（true=已占用）
        var occupied = new bool[rows, cols];

        // 先放置插件到预设位置（插件可能占据更大空间，优先分配）
        foreach (var plugin in expandedPlugins)
        {
            int pRow = plugin.GridRow;
            int pCol = plugin.GridColumn;
            int pRowSpan = plugin.GridRowSpan;
            int pColSpan = plugin.GridColSpan;

            // 如果位置无效或已占用，寻找新位置
            if (pRow < 0 || pCol < 0 || pRow + pRowSpan > rows || pCol + pColSpan > cols
                || IsAreaOccupied(occupied, Math.Max(0, pRow), Math.Max(0, pCol), pRowSpan, pColSpan))
            {
                var pos = FindFreePosition(occupied, rows, cols, pRowSpan, pColSpan);
                if (pos.row < 0)
                {
                    // 找不到位置，扩展行
                    while (true)
                    {
                        IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
                        rows++;
                        var newOccupied = new bool[rows, cols];
                        Array.Copy(occupied, newOccupied, occupied.Length);
                        occupied = newOccupied;
                        pos = FindFreePosition(occupied, rows, cols, pRowSpan, pColSpan);
                        if (pos.row >= 0) break;
                    }
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

        // 放置快捷方式图标（1x1占位）
        foreach (var item in _items)
        {
            // 查找预设位置
            int targetCell = -1;
            if (_config.ShortcutPositions != null && _config.ShortcutPositions.ContainsKey(item.LinkPath))
            {
                targetCell = _config.ShortcutPositions[item.LinkPath];
            }

            int row, col;
            if (targetCell >= 0)
            {
                row = targetCell / cols;
                col = targetCell % cols;
                // 如果预设位置无效或已被插件占用，寻找新位置
                if (row >= rows || col >= cols || occupied[row, col])
                {
                    var pos = FindFreePosition(occupied, rows, cols, 1, 1);
                    if (pos.row < 0)
                    {
                        while (true)
                        {
                            IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
                            rows++;
                            var newOccupied = new bool[rows, cols];
                            Array.Copy(occupied, newOccupied, occupied.Length);
                            occupied = newOccupied;
                            pos = FindFreePosition(occupied, rows, cols, 1, 1);
                            if (pos.row >= 0) break;
                        }
                    }
                    row = pos.row;
                    col = pos.col;
                }
            }
            else
            {
                var pos = FindFreePosition(occupied, rows, cols, 1, 1);
                if (pos.row < 0)
                {
                    while (true)
                    {
                        IconGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconCell) });
                        rows++;
                        var newOccupied = new bool[rows, cols];
                        Array.Copy(occupied, newOccupied, occupied.Length);
                        occupied = newOccupied;
                        pos = FindFreePosition(occupied, rows, cols, 1, 1);
                        if (pos.row >= 0) break;
                    }
                }
                row = pos.row;
                col = pos.col;
            }

            // 确保字典不为 null
            if (_config.ShortcutPositions == null) _config.ShortcutPositions = new();
            occupied[row, col] = true;
            _config.ShortcutPositions[item.LinkPath] = row * cols + col;

            var cell = BuildCell(item);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            IconGrid.Children.Add(cell);
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

    /// <summary>构建网格中的快捷方式单元格（支持拖拽）</summary>
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

        // 设置图标标识用于拖拽
        border.SetValue(DragIdProperty, item.LinkPath);
        border.SetValue(DragTypeProperty, "shortcut");

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

    /// <summary>按当前图标+插件数量计算展开后面板的最终尺寸，并夹取到工作区剩余空间（不足则滚动）。</summary>
    private void RecomputeTargets()
    {
        var d = S.Data;
        var wa = SystemParameters.WorkArea;
        double cellW = IconCell, cellH = IconCell;
        int cols = Math.Max(1, EffectiveCols);

        // 计算图标所需行数
        int iconCount = _items.Count;
        int iconRows = (int)Math.Ceiling(iconCount / (double)cols);

        // 计算插件占用的最大行（考虑插件的位置和跨度）
        int pluginMaxRow = 0;
        if (_config.Plugins != null)
        {
            foreach (var p in _config.Plugins.Where(pl => pl.ShowOnExpanded && pl.Type != FolderPluginType.None))
            {
                int pluginEndRow = p.GridRow >= 0 ? p.GridRow + p.GridRowSpan : 0;
                pluginMaxRow = Math.Max(pluginMaxRow, pluginEndRow);
            }
        }

        // 行数= max(设置的最小行数, 图标行数, 插件最大行, 1)
        int rows = Math.Max(EffectiveRows, iconRows);
        rows = Math.Max(rows, pluginMaxRow);
        rows = Math.Max(rows, 5); // 至少5行以容纳插件
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
        _slotExpanded?.GifTimer?.Start(); // 展开面板可见时恢复其 GIF 动画
        Panel.Width = CollapsedW;
        Panel.Height = CollapsedH;

        // 确保折叠态插件可见且透明度为1，参与展开动画
        PluginHostCollapsed.Visibility = Visibility.Visible;
        PluginHostCollapsed.Opacity = 1;
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
            PluginHostCollapsed.Opacity = 1;  // 展开时插件从1渐隐到0
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
                PluginHostCollapsed.Opacity = 0;
                PluginHostCollapsed.Visibility = Visibility.Collapsed; // 展开时隐藏折叠态插件
                if (!IsMouseOver) _collapseTimer.Start();
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
        if (_expanded && !_animating && !_contextMenuOpen && !_settingsOpen && !_gridItemDragging)
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
        Left = _winLeftStart + (cur.X - _dragScreenStart.X) / _dpiScaleX;
        Top = _winTopStart + (cur.Y - _dragScreenStart.Y) / _dpiScaleY;
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
        if (_config.Plugins == null) return;

        // 折叠态：尺寸为 CollapsedW × CollapsedH（FolderChip）
        // 折叠态插件仍然使用角落定位方式
        double cw = CollapsedW, ch = CollapsedH;

        foreach (var p in _config.Plugins)
        {
            if (p.Type == FolderPluginType.None) continue;
            if (p.ShowOnCollapsed)
                PluginHostCollapsed.Children.Add(RenderPlugin(p, true, cw, ch));
        }

        // 展开态插件现在通过 BuildGrid 方法渲染到 IconGrid 中（网格布局）
        // 不再通过 PluginHostExpanded 渲染，以避免重复
        // BuildGrid 在展开时会自动处理展开态插件的渲染
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

    /// <summary>拖拽经过网格：计算目标位置并显示放置指示</summary>
    private void IconGrid_DragOver(object sender, System.Windows.DragEventArgs e)
    {
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

        // 刻度弧（270° 量程，从 135° 到 405°）
        var arcPath = new System.Windows.Shapes.Path();
        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            double cx = size / 2, cy = size / 2, r = size * 0.38;
            var start = PointOnCircle(cx, cy, r, 135);
            var end = PointOnCircle(cx, cy, r, 135 + 270);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
        }
        geom.Freeze();
        arcPath.Data = geom;
        arcPath.Stroke = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        arcPath.StrokeThickness = size * 0.08;
        canvas.Children.Add(arcPath);

        // 动态数值弧
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
        void tick(object? s, EventArgs e)
        {
            // 每 2 秒换一个新"目标值"
            if (rnd.NextDouble() < 0.5) target = rnd.Next(5, 95);
            val = val * 0.65 + target * 0.35; // 平滑
            int pct = (int)Math.Clamp(val, 0, 100);
            double angleDeg = 135 + pct * 2.7; // 0→135°, 100→405°
            double cx = size / 2, cy = size / 2, r = size * 0.38;
            var fgGeom = new StreamGeometry();
            using (var ctx = fgGeom.Open())
            {
                var start = PointOnCircle(cx, cy, r, 135);
                var end = PointOnCircle(cx, cy, r, angleDeg);
                bool isLarge = angleDeg - 135 > 180;
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, isLarge, SweepDirection.Clockwise, true, false);
            }
            fgGeom.Freeze();
            fg.Data = fgGeom;
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

    // ---- 工具：角度→点 ----
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

    /// <summary>按裁剪区域对单帧取景；优先使用文件夹级裁剪配置（如果有），否则使用主题的裁剪配置；Stretch=Fill 保证选区精确铺满。</summary>
    private BitmapSource CropFrame(BitmapSource src, bool expanded, ThemeConfig theme)
    {
        bool hasCrop = _config.HasFolderImageCrop || _config.HasFolderImageCropExpanded
            ? (expanded ? _config.HasFolderImageCropExpanded : _config.HasFolderImageCrop)
            : (expanded ? theme.HasImageCropExpanded : theme.HasImageCrop);
        if (!hasCrop) return src;

        // 优先使用文件夹级裁剪配置
        double nx, ny, nw, nh;
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
            bool isGif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            if (isGif)
            {
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