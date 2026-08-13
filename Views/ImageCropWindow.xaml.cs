using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DeskFolder.Views;

/// <summary>
/// 图片主题裁剪对话框（折叠态 / 展开态独立裁剪）。
/// 折叠态与展开态在桌面上使用各自独立的图片控件，因此这里让用户分别为两个状态
/// 拖拽 / 缩放一个选框来选择要显示的图片区域，互不干扰。
/// 渲染采用 Stretch=Fill：选框区域按各自容器比例精确铺满，不存在二次居中裁切，
/// 因此本对话框的预览（CollPreview / ExpPreview）与桌面实际显示完全一致。
/// 关键：选框宽高比锁定为对应面板（折叠=collW×collH / 展开=expW×expH）的比例，
/// 因此裁剪后 Stretch=Fill 填充面板时不会发生拉伸变形。
/// 返回折叠态裁剪 <see cref="CollapsedCrop"/> 与展开态裁剪 <see cref="ExpandedCrop"/>（null=显示整图）。
/// </summary>
public partial class ImageCropWindow : Window
{
    private readonly string _path;
    private readonly double _collW, _collH, _expW, _expH;
    private readonly BitmapImage _src;

    // 视口与图片显示布局
    private const double ViewW = 540, ViewH = 380;
    private double _s, _imgX, _imgY, _dispW, _dispH;

    // 两个状态的裁剪矩形（归一化 0-1，相对原图；null = 整图）
    private Rect? _collCrop, _expCrop;
    private int _edit; // 0 = 折叠态，1 = 展开态（当前编辑哪个）

    // 当前编辑态的可拖拽选框（归一化）
    private double _nx, _ny, _nw, _nh;
    private const double MinFrac = 0.05;

    // 可视化元素
    private readonly Image _img = new();
    private readonly Rectangle _refRect = new();   // 另一态的参考框（不可编辑）
    private readonly Rectangle _cropBorder = new();
    private readonly Rectangle _cropBody = new();
    private readonly Rectangle _hTL = new(), _hTR = new(), _hBL = new(), _hBR = new();

    // 拖拽状态
    private string? _mode;
    private double _startMx, _startMy, _startNx, _startNy, _startNw, _startNh;

    public Rect? CollapsedCrop { get; private set; }
    public Rect? ExpandedCrop { get; private set; }

    /// <summary>裁剪编辑范围：0=同时编辑折叠/展开两态，1=仅折叠态，2=仅展开态（另态保持原值不变）。</summary>
    private readonly int _editState;

    /// <summary>比例来源：具体文件夹名（按该文件夹行列锁定）或 null（全局默认行列锁定）。
    /// 用于向用户明示当前裁剪比例按谁的尺寸锁定，避免"全局主题裁剪"被误用到行列不同的文件夹。</summary>
    private readonly string? _ratioSource;

    public ImageCropWindow(string imagePath, Rect? collCrop, Rect? expCrop,
        double collW, double collH, double expW, double expH, int editState = 0,
        string? ratioSource = null)
    {
        _path = imagePath;
        _collW = collW; _collH = collH; _expW = expW; _expH = expH;
        _collCrop = collCrop; _expCrop = expCrop;
        _editState = editState;
        _ratioSource = ratioSource;
        _edit = editState == 2 ? 1 : 0; // 内部 _edit：0=折叠，1=展开

        try
        {
            _src = new BitmapImage();
            _src.BeginInit();
            _src.UriSource = new Uri(imagePath, UriKind.Absolute);
            _src.CacheOption = BitmapCacheOption.OnLoad;
            _src.EndInit();
            _src.Freeze();
        }
        catch
        {
            _src = new BitmapImage();
        }

        InitializeComponent();

        // 默认选中当前编辑态对应的单选。
        // 注意：绝不能靠 XAML 的 IsChecked="True" 设默认态——InitializeComponent 解析到该 RadioButton 时
        // 会提前同步触发 Checked 事件，而 ModeHint 等后续控件尚未赋值，会 NullReferenceException
        // （即"裁剪展开态图片"报错的同一类构造期坑）。此处放在 InitializeComponent 之后，所有命名控件
        // 均已就绪，触发 Checked 事件安全。
        if (_edit == 1) ExpandedRadio.IsChecked = true;
        else CollapsedRadio.IsChecked = true;

        // 单态编辑（仅折叠 / 仅展开）：锁定对应单选并禁用另一态，避免误改
        if (_editState == 1)
        {
            CollapsedRadio.IsChecked = true;
            ExpandedRadio.IsEnabled = false;
        }
        else if (_editState == 2)
        {
            ExpandedRadio.IsChecked = true;
            CollapsedRadio.IsEnabled = false;
        }

        // 预览框尺寸：按各自真实面板比例绘制（不再是硬编码正方形），所见即所得
        double car = _collW / Math.Max(1, _collH);
        double cph = 100; double cpw = cph * car;
        if (cpw > 230) { cpw = 230; cph = 230 / car; }
        CollPreviewBox.Width = cpw; CollPreviewBox.Height = cph;
        double ar = _expW / Math.Max(1, _expH);
        double ph = 100; double pw = ph * ar;
        if (pw > 230) { pw = 230; ph = 230 / ar; }
        ExpPreviewBox.Width = pw; ExpPreviewBox.Height = ph;

        string srcLabel = string.IsNullOrWhiteSpace(_ratioSource)
            ? "全局默认（与具体文件夹无关）"
            : $"文件夹「{_ratioSource}」";
        InfoText.Text = $"折叠态尺寸：{collW:0}×{collH:0}　展开态尺寸：{expW:0}×{expH:0}" +
                        $"　原图：{_src.PixelWidth}×{_src.PixelHeight}（像素）。" +
                        $"裁剪比例按 {srcLabel} 的面板尺寸锁定。";

        // 全局主题裁剪：比例按全局默认行列锁定，可能与具体文件夹的实际展开比例不同，
        // 给出明确提醒，引导用户在对应文件夹的「外观设置」中裁剪以精确匹配。
        if (string.IsNullOrWhiteSpace(_ratioSource))
        {
            WarnText.Visibility = Visibility.Visible;
            WarnText.Text = "⚠ 当前为「全局主题」裁剪：展开比例按全局默认行列锁定，" +
                            "可能与你某个文件夹的实际展开比例不同。若需与特定文件夹精确匹配，" +
                            "请在该文件夹右键 → 外观设置 → 右键主题 → 编辑主题 → 裁剪。";
        }

        BuildScene();
        ComputeLayout();
        LoadActiveRect();
        ApplyModeColor();
        Redraw();

        // 安全网：WPF 构造期内 RadioButton 的 Checked 事件可能延迟或重复触发，
        // 导致 Mode_Collapsed/Mode_Expanded 在上述 LoadActiveRect 之后又重置了 _edit 和选框坐标。
        // 在 Loaded 优先级（窗口布局完成后）重新应用一次，确保最终显示的比例始终正确。
        Loaded += (s, e) =>
        {
            // 恢复 editState 对应的正确编辑态（防止被延迟事件翻转）
            _edit = _editState == 2 ? 1 : 0;
            if (_editState == 2) ExpandedRadio.IsChecked = true;
            else if (_editState == 1) CollapsedRadio.IsChecked = true;
            else CollapsedRadio.IsChecked = true; // editState=0（同时编辑）默认折叠态
            LoadActiveRect();
            ApplyModeColor();
            Redraw();
        };
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>计算图片在视口中的缩放与左上角偏移（等比居中适配 540×380）。</summary>
    private void ComputeLayout()
    {
        if (_src.PixelWidth <= 0 || _src.PixelHeight <= 0)
        {
            _s = 1; _dispW = ViewW; _dispH = ViewH; _imgX = 0; _imgY = 0;
            return;
        }
        _s = Math.Min(ViewW / _src.PixelWidth, ViewH / _src.PixelHeight);
        _dispW = _src.PixelWidth * _s;
        _dispH = _src.PixelHeight * _s;
        _imgX = (ViewW - _dispW) / 2;
        _imgY = (ViewH - _dispH) / 2;
        ApplyImageLayout();
    }

    /// <summary>把计算出的等比显示尺寸 / 居中偏移应用到原图 Image。</summary>
    private void ApplyImageLayout()
    {
        _img.Width = _dispW;
        _img.Height = _dispH;
        Canvas.SetLeft(_img, _imgX);
        Canvas.SetTop(_img, _imgY);
    }

    private void BuildScene()
    {
        _img.Stretch = Stretch.Fill;
        _img.Source = _src;
        _img.IsHitTestVisible = false;
        Canvas.Children.Add(_img);

        // 另一态参考框（虚线，不可编辑）
        _refRect.StrokeThickness = 2;
        _refRect.StrokeDashArray = new DoubleCollection { 4, 3 };
        _refRect.Fill = Brushes.Transparent;
        _refRect.IsHitTestVisible = false;
        _refRect.Opacity = 0.7;
        Canvas.Children.Add(_refRect);

        // 当前编辑态选框边框
        _cropBorder.StrokeThickness = 2;
        _cropBorder.Fill = Brushes.Transparent;
        _cropBorder.IsHitTestVisible = false;
        _cropBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 2, Opacity = 0.8, ShadowDepth = 0 };
        Canvas.Children.Add(_cropBorder);

        // 选框内部透明命中区（拖动移动）
        _cropBody.Fill = Brushes.Transparent;
        _cropBody.MouseLeftButtonDown += Body_MouseLeftButtonDown;
        Canvas.Children.Add(_cropBody);

        // 四角缩放手柄
        foreach (var h in new[] { _hTL, _hTR, _hBL, _hBR })
        {
            h.Width = 12; h.Height = 12;
            h.Fill = new SolidColorBrush(Colors.White);
            h.StrokeThickness = 2;
            h.Cursor = System.Windows.Input.Cursors.Cross;
            h.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            Canvas.Children.Add(h);
        }
    }

    private void LoadActiveRect()
    {
        var c = _edit == 0 ? _collCrop : _expCrop;
        if (c.HasValue) { _nx = c.Value.X; _ny = c.Value.Y; _nw = c.Value.Width; _nh = c.Value.Height; }
        else { _nx = 0; _ny = 0; _nw = 1; _nh = 1; }
        // 始终按目标面板比例吸附选框：无论是否已有裁剪，选框从一开始即与当前编辑态的面板比例一致
        SnapToAspect(ref _nx, ref _ny, ref _nw, ref _nh);
    }

    /// <summary>当前编辑态面板在归一化坐标系下的目标宽高比（nw/nh）。
    /// 裁剪坐标按原图宽/高分别归一化，故需乘以 (srcH/srcW) 才能换算到与面板像素比一致。</summary>
    private double ActiveAspectNorm()
    {
        double panelW = _edit == 0 ? _collW : _expW;
        double panelH = _edit == 0 ? _collH : _expH;
        if (panelW <= 0 || panelH <= 0) return 1;
        if (_src.PixelWidth <= 0 || _src.PixelHeight <= 0) return 1;
        // 注意：PixelHeight/PixelWidth 均为 int，必须转 double 再做除法，否则整数除法 (1190/1264)=0
        // 会把整个比例算成 0，导致 SnapToAspect 退化成正方形比例（展开态明显变方、折叠态因本身近方形而"看似正常"）
        double a = (panelW / panelH) * ((double)_src.PixelHeight / _src.PixelWidth);
        return a > 0 ? a : 1;
    }

    /// <summary>把裁剪矩形吸附到目标比例：保持中心、在 [0,1] 内尽量保留面积，避免拉伸。</summary>
    private void SnapToAspect(ref double nx, ref double ny, ref double nw, ref double nh)
    {
        double a = ActiveAspectNorm();
        double w = nw, h = w / a;
        if (h > 1) { h = 1; w = h * a; }
        if (w > 1) { w = 1; h = w / a; }
        w = Math.Max(MinFrac, w);
        h = Math.Max(MinFrac, h);
        if (w > 1) w = 1;
        if (h > 1) h = 1;
        double cx = nx + nw / 2, cy = ny + nh / 2;
        nx = Clamp(cx - w / 2, 0, 1 - w);
        ny = Clamp(cy - h / 2, 0, 1 - h);
        nw = w; nh = h;
    }

    private void SetActiveCrop(Rect r)
    {
        if (_edit == 0) _collCrop = r; else _expCrop = r;
    }

    private void ApplyModeColor()
    {
        var stroke = new SolidColorBrush(_edit == 0
            ? Color.FromRgb(0, 120, 215)   // 折叠态：蓝
            : Color.FromRgb(230, 126, 34)); // 展开态：橙
        _cropBorder.Stroke = stroke;
        foreach (var h in new[] { _hTL, _hTR, _hBL, _hBR }) h.Stroke = stroke;
        ModeHint.Text = _edit == 0
            ? "正在编辑：折叠态（150×150 正方形，已锁定比例防拉伸）"
            : $"正在编辑：展开态（{_expW:0}×{_expH:0}，已锁定比例防拉伸）";
    }

    /// <summary>把指定裁剪区域（归一化）从原图裁出，用于预览（null=整图）。</summary>
    private BitmapSource Cropped(Rect? crop)
    {
        if (!crop.HasValue || _src.PixelWidth <= 0) return _src;
        var r = crop.Value;
        int x = (int)Math.Round(r.X * _src.PixelWidth);
        int y = (int)Math.Round(r.Y * _src.PixelHeight);
        int w = (int)Math.Round(r.Width * _src.PixelWidth);
        int h = (int)Math.Round(r.Height * _src.PixelHeight);
        x = Math.Max(0, Math.Min(x, _src.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, _src.PixelHeight - 1));
        w = Math.Max(1, Math.Min(w, _src.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, _src.PixelHeight - y));
        var cb = new CroppedBitmap(_src, new Int32Rect(x, y, w, h));
        if (_src.IsFrozen && !cb.IsFrozen) cb.Freeze();
        return cb;
    }

    /// <summary>重新摆放所有可视元素并刷新两个预览。</summary>
    private void Redraw()
    {
        // 当前编辑态选框
        double cx = _imgX + _nx * _dispW;
        double cy = _imgY + _ny * _dispH;
        double cw = _nw * _dispW;
        double ch = _nh * _dispH;

        Canvas.SetLeft(_cropBorder, cx); Canvas.SetTop(_cropBorder, cy);
        _cropBorder.Width = cw; _cropBorder.Height = ch;
        Canvas.SetLeft(_cropBody, cx); Canvas.SetTop(_cropBody, cy);
        _cropBody.Width = cw; _cropBody.Height = ch;

        PlaceHandle(_hTL, cx, cy);
        PlaceHandle(_hTR, cx + cw, cy);
        PlaceHandle(_hBL, cx, cy + ch);
        PlaceHandle(_hBR, cx + cw, cy + ch);

        // 另一态参考框
        var refCrop = _edit == 0 ? _expCrop : _collCrop;
        if (refCrop.HasValue)
        {
            double rx = _imgX + refCrop.Value.X * _dispW;
            double ry = _imgY + refCrop.Value.Y * _dispH;
            double rw = refCrop.Value.Width * _dispW;
            double rh = refCrop.Value.Height * _dispH;
            Canvas.SetLeft(_refRect, rx); Canvas.SetTop(_refRect, ry);
            _refRect.Width = rw; _refRect.Height = rh;
            _refRect.Stroke = new SolidColorBrush(_edit == 0
                ? Color.FromRgb(230, 126, 34)  // 另一态为展开态→橙
                : Color.FromRgb(0, 120, 215)); // 另一态为折叠态→蓝
            _refRect.Visibility = Visibility.Visible;
        }
        else _refRect.Visibility = Visibility.Collapsed;

        // 实时预览（按各自容器比例填充，所见即所得）
        CollPreview.Source = Cropped(_collCrop);
        ExpPreview.Source = Cropped(_expCrop);
    }

    private static void PlaceHandle(Rectangle h, double x, double y)
    {
        Canvas.SetLeft(h, x - h.Width / 2);
        Canvas.SetTop(h, y - h.Height / 2);
    }

    // ---------------- 模式切换 ----------------

    private void Mode_Collapsed(object sender, RoutedEventArgs e)
    {
        // 防御：InitializeComponent 期间若 Checked 事件被提前触发，ModeHint 等可能尚未赋值
        if (ModeHint == null) return;
        if (_edit == 0) return;
        _edit = 0; LoadActiveRect(); ApplyModeColor(); Redraw();
    }

    private void Mode_Expanded(object sender, RoutedEventArgs e)
    {
        if (ModeHint == null) return;
        if (_edit == 1) return;
        _edit = 1; LoadActiveRect(); ApplyModeColor(); Redraw();
    }

    // ---------------- 拖拽交互 ----------------

    private void Body_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mode = "move";
        _startMx = e.GetPosition(Canvas).X;
        _startMy = e.GetPosition(Canvas).Y;
        _startNx = _nx; _startNy = _ny; _startNw = _nw; _startNh = _nh;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mode = (sender == _hTL) ? "tl" : (sender == _hTR) ? "tr" : (sender == _hBL) ? "bl" : "br";
        _startMx = e.GetPosition(Canvas).X;
        _startMy = e.GetPosition(Canvas).Y;
        _startNx = _nx; _startNy = _ny; _startNw = _nw; _startNh = _nh;
        Canvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_mode == null) return;
        double dx = (e.GetPosition(Canvas).X - _startMx) / _dispW;
        double dy = (e.GetPosition(Canvas).Y - _startMy) / _dispH;

        if (_mode == "move")
        {
            _nx = Clamp(_startNx + dx, 0, 1 - _nw);
            _ny = Clamp(_startNy + dy, 0, 1 - _nh);
        }
        else if (_mode == "br" || _mode == "tl" || _mode == "tr" || _mode == "bl")
        {
            // 锁定比例缩放：宽度/高度按目标面板比例联动，避免裁剪后填充拉伸
            ResizeWithAspect(_mode, dx, dy);
        }
        SetActiveCrop(new Rect(_nx, _ny, _nw, _nh));
        Redraw();
    }

    /// <summary>按锁定比例缩放裁剪框（四角手柄通用）。以主导轴驱动宽，再按目标比例推导高，
    /// 并在 [0,1] 与最小尺寸内夹紧；固定角（被拖动手柄的对侧）保持不动。</summary>
    private void ResizeWithAspect(string mode, double dx, double dy)
    {
        double a = ActiveAspectNorm();

        // 固定角（被拖动手柄的对侧顶点）
        double fixedX, fixedY;
        if (mode == "br") { fixedX = _startNx; fixedY = _startNy; }
        else if (mode == "tl") { fixedX = _startNx + _startNw; fixedY = _startNy + _startNh; }
        else if (mode == "tr") { fixedX = _startNx; fixedY = _startNy + _startNh; }
        else /* bl */ { fixedX = _startNx + _startNw; fixedY = _startNy; }

        // 取主导轴：横向位移直接驱动宽；纵向位移换算为宽（高×比例）后取绝对值较大者
        double rightSide = (mode == "br" || mode == "tr") ? 1 : -1;   // +dx 是否使宽度增大
        double bottomSide = (mode == "br" || mode == "bl") ? 1 : -1;  // +dy 是否使高度增大
        double dxW = rightSide * dx;          // 横向位移折算的宽度增量
        double dyW = bottomSide * dy * a;     // 纵向位移折算的宽度增量（高→宽）
        double dW = Math.Abs(dxW) >= Math.Abs(dyW) ? dxW : dyW;

        // 各手柄在 x / y 方向允许的最大宽/高（固定边到对侧边界的距离）
        double maxNw = (mode == "tl" || mode == "bl") ? fixedX : 1 - fixedX;
        double maxNh = (mode == "tl" || mode == "tr") ? fixedY : 1 - fixedY;

        double nw = Clamp(_startNw + dW, MinFrac, maxNw);
        double nh = nw / a;
        if (nh > maxNh) { nh = maxNh; nw = nh * a; }
        if (nh < MinFrac) { nh = MinFrac; nw = nh * a; nw = Clamp(nw, MinFrac, maxNw); }

        double nx = (mode == "tl" || mode == "bl") ? fixedX - nw : fixedX;
        double ny = (mode == "tr" || mode == "tl") ? fixedY - nh : fixedY;

        _nw = nw; _nh = nh; _nx = nx; _ny = ny;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode != null)
        {
            _mode = null;
            Canvas.ReleaseMouseCapture();
        }
    }

    private static bool NearFull(Rect? r)
    {
        if (!r.HasValue) return true;
        var v = r.Value;
        return v.X < 1e-3 && v.Y < 1e-3 && v.Width > 0.999 - 1e-3 && v.Height > 0.999 - 1e-3;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        CollapsedCrop = NearFull(_collCrop) ? null : _collCrop;
        ExpandedCrop = NearFull(_expCrop) ? null : _expCrop;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
