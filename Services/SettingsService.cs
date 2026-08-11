using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DeskFolder.Services;

/// <summary>主题类型：决定应用文件夹（折叠图标 / 展开面板）如何绘制外观。</summary>
public enum ThemeMode
{
    /// <summary>填充颜色：纯色背景 + 透明度（半透明 / 玻璃材质等）</summary>
    Fill,
    /// <summary>简约方框：完全透明背景，仅画一个带圆角的边框方框</summary>
    BorderOnly,
    /// <summary>图片背景：用用户导入的图片（支持 GIF 等动图）作为背景</summary>
    Image,
    /// <summary>渐变背景：线性/径向双色渐变，可调节角度和透明度</summary>
    Gradient,
    /// <summary>霓虹风格：细发光边框 + 深色背景，赛博朋克风格</summary>
    Neon,
    /// <summary>玻璃拟态：半透明 + 动态内发光 + 高光边</summary>
    Glass,
    /// <summary>亚克力（Mica 风格）：多层叠色 + 噪点，模拟 Windows 11 Mica Alt</summary>
    Acrylic,
    /// <summary>折纸风格：多层阴影堆叠 + 微位移，拟物折纸效果</summary>
    Paper,
    /// <summary>浮雕风格：内阴影凹入效果，3D 按钮观感</summary>
    Emboss
}

/// <summary>图片背景的子模式：单图（折叠/展开共用同一组图）/ 多图（折叠/展开各自一组图）。</summary>
public enum ImageLayoutMode
{
    /// <summary>单图模式：折叠与展开使用同一组图片，轮播/随机时两者保持同步（显示同一张）。</summary>
    Single,
    /// <summary>多图模式：折叠与展开各自一组图片，播放方式/间隔可分别设置。</summary>
    Multi
}

/// <summary>图片播放方式：不轮播 / 轮流播放 / 随机播放。</summary>
public enum ImagePlayMode
{
    /// <summary>不轮播：固定显示列表第一张。</summary>
    Off,
    /// <summary>轮流播放：按顺序循环切换。</summary>
    Sequential,
    /// <summary>随机播放：随机切换到另一张。</summary>
    Random
}

/// <summary>一组图片及其播放设置（单图模式一组、多图模式折叠/展开各一组）。</summary>
public class ImagePlaylist
{
    /// <summary>图片路径列表（绝对路径；可多选导入）。为空表示尚未导入图片。</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>播放方式：不轮播 / 轮流 / 随机。</summary>
    public ImagePlayMode Play { get; set; } = ImagePlayMode.Off;

    /// <summary>切换间隔（分钟）；轮播/随机时每隔这么多分钟切到下一张。</summary>
    public int IntervalMinutes { get; set; } = 5;
}

/// <summary>单个主题：决定应用文件夹（折叠图标 / 展开面板）的外观。</summary>
public class ThemeConfig
{
    /// <summary>稳定标识（用于 CurrentThemeId 引用，重命名也不影响）</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>内置主题标识；null 表示用户自定义主题（可删除）</summary>
    public string? BuiltInId { get; set; }
    public string Name { get; set; } = "主题";
    /// <summary>主题类型：填充颜色 / 简约方框 / 图片背景</summary>
    public ThemeMode Mode { get; set; } = ThemeMode.Fill;
    /// <summary>背景颜色（十六进制 #RRGGBB 或 #AARRGGBB，填充模式使用）</summary>
    public string BackgroundColor { get; set; } = "#000000";
    /// <summary>背景不透明度 0-1（填充模式 = 背景透明度；图片模式 = 图片透明度）</summary>
    public double BackgroundOpacity { get; set; } = 0.6;
    /// <summary>应用文件夹圆角大小（像素）</summary>
    public double CornerRadius { get; set; } = 16;
    /// <summary>方框颜色（简约方框模式的边框颜色）</summary>
    public string BorderColor { get; set; } = "#FFFFFF";
    /// <summary>方框宽度（边框粗细，像素）</summary>
    public double BorderThickness { get; set; } = 2;
    /// <summary>方框类型：0 实线 / 1 虚线 / 2 点线 / 3 双线 / 4 Windows 11 风格</summary>
    public int BorderStyle { get; set; } = 0;
    /// <summary>图片背景路径（图片模式；GIF 等受 WPF 支持的格式均可，动图会自动播放）</summary>
    public string ImagePath { get; set; } = "";
    /// <summary>图片裁剪区域（归一化 0-1，相对原图像素坐标）：X/Y=左上角，W/H=宽高比例；全为 null 表示不裁剪（使用整图）。</summary>
    public double? ImageCropX { get; set; } = null;
    /// <summary>图片裁剪区域左上角 X（归一化 0-1）</summary>
    public double? ImageCropY { get; set; } = null;
    /// <summary>图片裁剪区域宽度（归一化 0-1）</summary>
    public double? ImageCropW { get; set; } = null;
    /// <summary>图片裁剪区域高度（归一化 0-1）</summary>
    public double? ImageCropH { get; set; } = null;

    /// <summary>是否存在有效裁剪区域（四个值齐全且宽高为正）；用于渲染与预览判断是否裁剪。</summary>
    [JsonIgnore]
    public bool HasImageCrop =>
        ImageCropX.HasValue && ImageCropY.HasValue && ImageCropW.HasValue && ImageCropH.HasValue
        && ImageCropW.Value > 0 && ImageCropH.Value > 0;

    /// <summary>展开态独立裁剪区域（归一化 0-1，相对原图像素坐标）。折叠态与展开态使用各自的图片控件，
    /// 因此可分别裁剪：折叠态用 ImageCrop*，展开态用 ImageCropExpanded*。全为 null 表示展开态显示整图。</summary>
    public double? ImageCropExpandedX { get; set; } = null;
    public double? ImageCropExpandedY { get; set; } = null;
    public double? ImageCropExpandedW { get; set; } = null;
    public double? ImageCropExpandedH { get; set; } = null;

    /// <summary>展开态是否存在有效裁剪区域。</summary>
    [JsonIgnore]
    public bool HasImageCropExpanded =>
        ImageCropExpandedX.HasValue && ImageCropExpandedY.HasValue
        && ImageCropExpandedW.HasValue && ImageCropExpandedH.HasValue
        && ImageCropExpandedW.Value > 0 && ImageCropExpandedH.Value > 0;

    // ---------------- 多图 / 轮播（图片背景模式） ----------------
    /// <summary>图片布局模式：单图（折叠/展开共用一组）/ 多图（各自一组）。默认单图（兼容旧版仅单图）。</summary>
    public ImageLayoutMode ImageLayout { get; set; } = ImageLayoutMode.Single;
    /// <summary>单图模式的图片组（折叠与展开共用、同步轮播）。</summary>
    public ImagePlaylist Single { get; set; } = new();
    /// <summary>多图模式的折叠态图片组（独立播放方式与间隔）。</summary>
    public ImagePlaylist Collapsed { get; set; } = new();
    /// <summary>多图模式的展开态图片组（独立播放方式与间隔）。</summary>
    public ImagePlaylist Expanded { get; set; } = new();

    /// <summary>取得某状态（折叠=false / 展开=true）应当使用的图片组：多图模式取对应组，单图模式取共用组。</summary>
    public ImagePlaylist PlaylistFor(bool expanded) =>
        ImageLayout == ImageLayoutMode.Multi ? (expanded ? Expanded : Collapsed) : Single;

    /// <summary>把旧版单图 ImagePath 迁移进单图组（仅当单图模式且 Single 为空时）。返回是否做了迁移。</summary>
    public bool MigrateLegacyImagePath()
    {
        if (ImageLayout == ImageLayoutMode.Single && Single.Paths.Count == 0
            && !string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
        {
            Single.Paths.Add(ImagePath);
            return true;
        }
        return false;
    }

    // ---------------- 渐变背景模式（ThemeMode.Gradient） ----------------
    /// <summary>渐变起始颜色（左上角/起点）</summary>
    public string GradientColorA { get; set; } = "#667EEA";
    /// <summary>渐变结束颜色（右下角/终点）</summary>
    public string GradientColorB { get; set; } = "#764BA2";
    /// <summary>渐变类型：0 线性 / 1 径向 / 2 对角线性 / 3 垂直 / 4 水平</summary>
    public int GradientType { get; set; } = 0;
    /// <summary>线性渐变角度（度，仅类型 0 有效）</summary>
    public double GradientAngle { get; set; } = 135;

    // ---------------- 霓虹风格（ThemeMode.Neon） ----------------
    /// <summary>霓虹发光颜色（边框光晕色）</summary>
    public string NeonGlowColor { get; set; } = "#00FFE1";
    /// <summary>霓虹背景色（通常为深色）</summary>
    public string NeonBgColor { get; set; } = "#0A0A0F";
    /// <summary>霓虹发光强度（0-3，越大光晕越宽）</summary>
    public double NeonGlowIntensity { get; set; } = 1.0;

    // ---------------- 玻璃拟态（ThemeMode.Glass） ----------------
    /// <summary>玻璃主色（叠加在背景之上的颜色）</summary>
    public string GlassTintColor { get; set; } = "#FFFFFF";
    /// <summary>玻璃高光边颜色（左上亮边）</summary>
    public string GlassHighlight { get; set; } = "#FFFFFFFF";
    /// <summary>玻璃饱和度（0-1，越大越像有色玻璃而非磨砂）</summary>
    public double GlassSaturation { get; set; } = 0.3;

    // ---------------- 亚克力 / Mica（ThemeMode.Acrylic） ----------------
    /// <summary>亚克力主色（叠层基础色）</summary>
    public string AcrylicTint { get; set; } = "#F3F3F3";
    /// <summary>亚克力噪点层不透明度（0-1，越大颗粒感越强）</summary>
    public double AcrylicNoise { get; set; } = 0.03;
    /// <summary>亚克力叠层透明度（0-1）</summary>
    public double AcrylicOpacity { get; set; } = 0.7;

    // ---------------- 折纸风格（ThemeMode.Paper） ----------------
    /// <summary>折纸主色（纸张颜色）</summary>
    public string PaperColor { get; set; } = "#FAF7F0";
    /// <summary>折纸折叠方向：0 左上 / 1 右上 / 2 左下 / 3 右下</summary>
    public int PaperFoldDirection { get; set; } = 0;
    /// <summary>折纸阴影强度（0-2，越大折痕越深）</summary>
    public double PaperShadowDepth { get; set; } = 1.0;

    // ---------------- 浮雕风格（ThemeMode.Emboss） ----------------
    /// <summary>浮雕底色</summary>
    public string EmbossColor { get; set; } = "#E8E8E8";
    /// <summary>浮雕凸起高度（0-8px，越大立体感越强）</summary>
    public double EmbossHeight { get; set; } = 3.0;

    // ---------------- 文字设置（文件夹名称文字：折叠态名称条 + 展开态标题） ----------------
    /// <summary>文字字体（字体族名称，如 "Microsoft YaHei UI"）；空字符串 = 跟随系统默认字体</summary>
    public string TextFont { get; set; } = "";
    /// <summary>文字大小（逻辑像素）；0 = 跟随各状态默认（折叠 11 / 展开 13）</summary>
    public double TextSize { get; set; } = 0;
    /// <summary>文字颜色（十六进制 #RRGGBB / #AARRGGBB）；空字符串 = 自动（填充/方框按背景亮度对比，图片模式白字）</summary>
    public string TextColor { get; set; } = "";
    /// <summary>文字位置：0 底部 / 1 居中 / 2 顶部（折叠态决定名称条位置；展开态 0=标题置底，其余=标题置顶）</summary>
    public int TextPosition { get; set; } = 0;
    /// <summary>折叠状态是否隐藏文件夹名称文字</summary>
    public bool HideTextCollapsed { get; set; } = false;
    /// <summary>展开状态是否隐藏文件夹标题文字</summary>
    public bool HideTextExpanded { get; set; } = false;
    /// <summary>文字是否加粗</summary>
    public bool TextBold { get; set; } = false;

    /// <summary>列表用预览画刷（按模式合成）；不参与序列化</summary>
    [JsonIgnore]
    public Brush PreviewBrush
    {
        get
        {
            if (Mode == ThemeMode.BorderOnly)
            {
                if (ThemeHelper.TryParseColor(BorderColor, out var c))
                    return new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
                return new SolidColorBrush(Colors.Transparent);
            }
            if (Mode == ThemeMode.Image)
            {
                // 用斜向灰阶渐变表示"图片"类型
                return new LinearGradientBrush(Colors.LightGray, Colors.Gray, 45);
            }
            // 填充模式
            if (ThemeHelper.TryParseColor(BackgroundColor, out var bc))
            {
                byte a = (byte)Math.Clamp(BackgroundOpacity * 255, 0, 255);
                return new SolidColorBrush(Color.FromArgb(a, bc.R, bc.G, bc.B));
            }
            return new SolidColorBrush(Colors.Transparent);
        }
    }
}

/// <summary>插件类型：任意文件夹均可挂载一个或多个装饰性插件，用于桌面美化。</summary>
public enum FolderPluginType
{
    /// <summary>不显示任何插件</summary>
    None,
    /// <summary>模拟时钟：表盘 + 时针/分针/秒针，随系统时间实时转动</summary>
    AnalogClock,
    /// <summary>数字时钟：大字体显示当前时间（时/分/秒）+ 日期</summary>
    DigitalClock,
    /// <summary>便签条：可编辑文字的便笺，粘贴在折叠图标角落</summary>
    StickyNote,
    /// <summary>CPU 仪表盘：实时显示 CPU 占用率的圆形仪表</summary>
    CpuGauge,
    /// <summary>天气徽章：显示城市天气图标 + 温度（本地配置，需手动填写）</summary>
    WeatherBadge,
    /// <summary>日历小方块：显示当前日期数字</summary>
    CalendarTile
}

/// <summary>插件网格尺寸类型：决定插件在网格中占用的单元格大小。</summary>
public enum PluginGridSize
{
    /// <summary>1×1：占用一个单元格（默认）</summary>
    Small,
    /// <summary>1×2：竖向占用两个单元格（如便签、长条时钟）</summary>
    Vertical,
    /// <summary>2×1：横向占用两个单元格</summary>
    Horizontal,
    /// <summary>2×2：占用四个单元格（如大时钟、天气面板）</summary>
    Large
}

/// <summary>单个插件配置：类型 + 位置 + 自定义参数。
/// 支持两种布局模式：角落定位（折叠态）和网格占位（展开态）。</summary>
public class FolderPlugin
{
    /// <summary>插件类型</summary>
    public FolderPluginType Type { get; set; } = FolderPluginType.None;
    /// <summary>折叠态位置：0 左上 / 1 右上 / 2 左下 / 3 右下</summary>
    public int CollapsedCorner { get; set; } = 1;
    /// <summary>展开态位置：0 左上 / 1 右上 / 2 左下 / 3 右下（兼容旧版角落定位）</summary>
    public int ExpandedCorner { get; set; } = 0;
    /// <summary>插件尺寸（像素，正方形边长）；0 = 按类型默认</summary>
    public double Size { get; set; } = 0;
    /// <summary>通用文字参数：便签内容 / 天气城市 / 时钟时区名称 等</summary>
    public string Text { get; set; } = "";
    /// <summary>通用颜色参数：便签背景色 / 时钟表盘色 / 仪表盘指针色 等</summary>
    public string Color { get; set; } = "";
    /// <summary>插件在折叠态是否显示</summary>
    public bool ShowOnCollapsed { get; set; } = true;
    /// <summary>插件在展开态是否显示</summary>
    public bool ShowOnExpanded { get; set; } = false;
    /// <summary>折叠态偏移 X（像素，相对角位置微调；正=向右，负=向左）</summary>
    public double CollapsedOffsetX { get; set; } = 0;
    /// <summary>折叠态偏移 Y（像素，相对角位置微调；正=向下，负=向上）</summary>
    public double CollapsedOffsetY { get; set; } = 0;
    /// <summary>展开态偏移 X（像素，相对角位置微调，仅角落模式使用）</summary>
    public double ExpandedOffsetX { get; set; } = 0;
    /// <summary>展开态偏移 Y（像素，相对角位置微调，仅角落模式使用）</summary>
    public double ExpandedOffsetY { get; set; } = 0;

    // ===== 网格布局相关（展开态网格使用） =====
    /// <summary>网格占位尺寸：1×1 / 1×2 / 2×1 / 2×2</summary>
    public PluginGridSize GridSize { get; set; } = PluginGridSize.Small;
    /// <summary>在展开网格中的起始行（0 基）；-1 表示未设置</summary>
    public int GridRow { get; set; } = -1;
    /// <summary>在展开网格中的起始列（0 基）；-1 表示未设置</summary>
    public int GridColumn { get; set; } = -1;
    /// <summary>在网格中的唯一标识（用于拖拽后更新位置时识别）</summary>
    public string GridId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>网格占位宽度（列数）</summary>
    [JsonIgnore]
    public int GridColSpan => GridSize switch
    {
        PluginGridSize.Horizontal => 2,
        PluginGridSize.Large => 2,
        _ => 1
    };

    /// <summary>网格占位高度（行数）</summary>
    [JsonIgnore]
    public int GridRowSpan => GridSize switch
    {
        PluginGridSize.Vertical => 2,
        PluginGridSize.Large => 2,
        _ => 1
    };
}

public class FolderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "文件夹";
    public List<string> Shortcuts { get; set; } = new();
    /// <summary>快捷方式图标在网格中的位置映射：key=快捷方式路径，value=网格索引（0 基）。
    /// 未指定位置的图标将按顺序填充到剩余空位。</summary>
    public Dictionary<string, int> ShortcutPositions { get; set; } = new();
    public double X { get; set; } = double.NaN; // 窗口位置（NaN = 自动）
    public double Y { get; set; } = double.NaN;
    /// <summary>每文件夹列数覆盖；null = 跟随全局 Columns</summary>
    public int? FolderColumns { get; set; } = null;
    /// <summary>每文件夹行数覆盖；null = 跟随全局 Rows</summary>
    public int? FolderRows { get; set; } = null;
    /// <summary>每文件夹折叠图标自由像素宽（拖动缩放写入）；null = 跟随默认折叠尺寸</summary>
    public double? FolderFoldW { get; set; } = null;
    /// <summary>每文件夹折叠图标自由像素高（拖动缩放写入）；null = 跟随默认折叠尺寸</summary>
    public double? FolderFoldH { get; set; } = null;
    /// <summary>每文件夹外观主题覆盖；null = 跟随全局当前主题</summary>
    public string? FolderThemeId { get; set; } = null;

    // ---------------- 裁剪配置覆盖（null = 跟随主题的裁剪设置） ----------------
    /// <summary>文件夹级折叠态裁剪区域 X（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropX { get; set; } = null;
    /// <summary>文件夹级折叠态裁剪区域 Y（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropY { get; set; } = null;
    /// <summary>文件夹级折叠态裁剪区域宽度（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropW { get; set; } = null;
    /// <summary>文件夹级折叠态裁剪区域高度（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropH { get; set; } = null;
    /// <summary>文件夹级折叠态是否存在有效裁剪区域（四个值齐全且宽高为正）</summary>
    [JsonIgnore]
    public bool HasFolderImageCrop =>
        FolderImageCropX.HasValue && FolderImageCropY.HasValue && FolderImageCropW.HasValue && FolderImageCropH.HasValue
        && FolderImageCropW.Value > 0 && FolderImageCropH.Value > 0;

    /// <summary>文件夹级展开态裁剪区域 X（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropExpandedX { get; set; } = null;
    /// <summary>文件夹级展开态裁剪区域 Y（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropExpandedY { get; set; } = null;
    /// <summary>文件夹级展开态裁剪区域宽度（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropExpandedW { get; set; } = null;
    /// <summary>文件夹级展开态裁剪区域高度（归一化 0-1）；null = 使用主题的裁剪设置</summary>
    public double? FolderImageCropExpandedH { get; set; } = null;
    /// <summary>文件夹级展开态是否存在有效裁剪区域</summary>
    [JsonIgnore]
    public bool HasFolderImageCropExpanded =>
        FolderImageCropExpandedX.HasValue && FolderImageCropExpandedY.HasValue
        && FolderImageCropExpandedW.HasValue && FolderImageCropExpandedH.HasValue
        && FolderImageCropExpandedW.Value > 0 && FolderImageCropExpandedH.Value > 0;

    /// <summary>清除文件夹级裁剪配置（恢复使用主题的裁剪设置）</summary>
    public void ClearFolderCrop()
    {
        FolderImageCropX = null; FolderImageCropY = null;
        FolderImageCropW = null; FolderImageCropH = null;
        FolderImageCropExpandedX = null; FolderImageCropExpandedY = null;
        FolderImageCropExpandedW = null; FolderImageCropExpandedH = null;
    }

    /// <summary>美化插件列表：每个文件夹可挂载多个装饰性插件（时钟 / 便签 / 日历等）。
    /// 与主题无关，任何主题下都可独立启用，用于桌面美化装饰。</summary>
    public List<FolderPlugin> Plugins { get; set; } = new();
}

public class AppSettingsData
{
    /// <summary>展开时每行图标数（列）</summary>
    public int Columns { get; set; } = 4;
    /// <summary>展开时每列图标数（行，超出可滚动）</summary>
    public int Rows { get; set; } = 3;
    /// <summary>展开/收起动画时长（毫秒）</summary>
    public int AnimationMs { get; set; } = 220;
    /// <summary>鼠标移入多少毫秒后才展开（防抖）</summary>
    public int HoverDelayMs { get; set; } = 150;
    /// <summary>单个图标单元格宽度</summary>
    public int CellWidth { get; set; } = 88;
    /// <summary>单个图标单元格高度</summary>
    public int CellHeight { get; set; } = 96;
    /// <summary>折叠图标内预览缩略图的行数（1-6）</summary>
    public int PreviewRows { get; set; } = 3;
    /// <summary>折叠图标内预览缩略图的列数（1-6）</summary>
    public int PreviewCols { get; set; } = 3;
    /// <summary>开机自启动（写入 HKCU\...\Run，由 StartupService 落实）</summary>
    public bool LaunchAtStartup { get; set; } = false;
    /// <summary>当前生效主题 Id</summary>
    public string CurrentThemeId { get; set; } = "";
    /// <summary>全部主题（内置 + 自定义）</summary>
    public List<ThemeConfig> Themes { get; set; } = new();
    public List<FolderConfig> Folders { get; set; } = new();
}

/// <summary>设置持久化：%APPDATA%\DeskFolder\settings.json</summary>
public class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolder");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>内置主题模板；新增风格主题时在此追加即可（按 BuiltInId 去重补齐）。</summary>
    private static readonly ThemeConfig[] BuiltInTemplates =
    {
        new ThemeConfig { BuiltInId = "semi",  Name = "半透明",   Mode = ThemeMode.Fill,       BackgroundColor = "#000000", BackgroundOpacity = 0.60, CornerRadius = 16 },
        new ThemeConfig { BuiltInId = "glass", Name = "玻璃材质", Mode = ThemeMode.Fill,       BackgroundColor = "#BFE3FF", BackgroundOpacity = 0.40, CornerRadius = 24 },
        new ThemeConfig { BuiltInId = "frame", Name = "简约方框", Mode = ThemeMode.BorderOnly, BorderColor = "#FFFFFF", BorderThickness = 2, BorderStyle = 0, CornerRadius = 16 },
        new ThemeConfig { BuiltInId = "image", Name = "图片背景", Mode = ThemeMode.Image,      ImagePath = "", BackgroundOpacity = 1.0, CornerRadius = 16 },
        // ---- 6 种美化主题 ----
        new ThemeConfig { BuiltInId = "gradient", Name = "极光渐变", Mode = ThemeMode.Gradient,
            GradientColorA = "#667EEA", GradientColorB = "#764BA2", GradientType = 2, GradientAngle = 135,
            BackgroundOpacity = 0.85, CornerRadius = 20 },
        new ThemeConfig { BuiltInId = "neon", Name = "赛博霓虹", Mode = ThemeMode.Neon,
            NeonGlowColor = "#00FFE1", NeonBgColor = "#0A0A0F", NeonGlowIntensity = 1.2,
            BackgroundOpacity = 0.75, CornerRadius = 14 },
        new ThemeConfig { BuiltInId = "glasstrue", Name = "冰霜玻璃", Mode = ThemeMode.Glass,
            GlassTintColor = "#FFFFFF", GlassHighlight = "#FFFFFFFF", GlassSaturation = 0.25,
            BackgroundOpacity = 0.80, CornerRadius = 24 },
        new ThemeConfig { BuiltInId = "acrylic", Name = "Mica 亚克力", Mode = ThemeMode.Acrylic,
            AcrylicTint = "#F3F3F3", AcrylicOpacity = 0.70, AcrylicNoise = 0.03,
            CornerRadius = 18 },
        new ThemeConfig { BuiltInId = "paper", Name = "复古折纸", Mode = ThemeMode.Paper,
            PaperColor = "#FAF7F0", PaperFoldDirection = 0, PaperShadowDepth = 1.2,
            CornerRadius = 14 },
        new ThemeConfig { BuiltInId = "emboss", Name = "浮雕经典", Mode = ThemeMode.Emboss,
            EmbossColor = "#E8E8E8", EmbossHeight = 3.5,
            CornerRadius = 12 },
    };

    public AppSettingsData Data { get; private set; } = new();

    public event Action? SettingsChanged;

    public static SettingsService Load()
    {
        var svc = new SettingsService();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                svc.Data = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
            }
        }
        catch { svc.Data = new AppSettingsData(); }
        svc.Data.Columns = Math.Clamp(svc.Data.Columns, 1, 12);
        svc.Data.Rows = Math.Clamp(svc.Data.Rows, 1, 12);
        svc.Data.PreviewRows = Math.Clamp(svc.Data.PreviewRows, 1, 6);
        svc.Data.PreviewCols = Math.Clamp(svc.Data.PreviewCols, 1, 6);
        foreach (var f in svc.Data.Folders)
        {
            if (f.FolderColumns.HasValue) f.FolderColumns = Math.Clamp(f.FolderColumns.Value, 1, 12);
            if (f.FolderRows.HasValue) f.FolderRows = Math.Clamp(f.FolderRows.Value, 1, 12);
            if (f.FolderFoldW.HasValue) f.FolderFoldW = Math.Clamp(f.FolderFoldW.Value, 60, 800);
            if (f.FolderFoldH.HasValue) f.FolderFoldH = Math.Clamp(f.FolderFoldH.Value, 60, 800);
        }
        // 旧版兼容：把主题里的单张 ImagePath 迁移进单图组（不影响已用新模型的主题）
        foreach (var t in svc.Data.Themes)
            t.MigrateLegacyImagePath();
        svc.EnsureDefaults();
        return svc;
    }

    /// <summary>保证内置主题始终存在（按 BuiltInId 补齐），并选定一个合法的当前主题。</summary>
    public void EnsureDefaults()
    {
        foreach (var tpl in BuiltInTemplates)
        {
            if (!Data.Themes.Any(t => t.BuiltInId == tpl.BuiltInId))
                Data.Themes.Add(new ThemeConfig
                {
                    BuiltInId = tpl.BuiltInId,
                    Name = tpl.Name,
                    Mode = tpl.Mode,
                    BackgroundColor = tpl.BackgroundColor,
                    BackgroundOpacity = tpl.BackgroundOpacity,
                    CornerRadius = tpl.CornerRadius,
                    BorderColor = tpl.BorderColor,
                    BorderThickness = tpl.BorderThickness,
                    BorderStyle = tpl.BorderStyle,
                    ImagePath = tpl.ImagePath
                });
        }
        if (Data.Themes.Count == 0)
            Data.Themes.Add(new ThemeConfig { Name = "半透明", BackgroundColor = "#000000", BackgroundOpacity = 0.6, CornerRadius = 16 });
        if (string.IsNullOrEmpty(Data.CurrentThemeId) || !Data.Themes.Any(t => t.Id == Data.CurrentThemeId))
            Data.CurrentThemeId = Data.Themes.First(t => t.BuiltInId == "semi").Id;
    }

    /// <summary>取得当前生效主题（保底返回首个主题，绝不返回 null）。</summary>
    public ThemeConfig GetCurrentTheme()
    {
        return Data.Themes.FirstOrDefault(t => t.Id == Data.CurrentThemeId)
            ?? Data.Themes.FirstOrDefault()
            ?? new ThemeConfig();
    }

    /// <summary>取得指定文件夹应使用的主题：优先文件夹自己的 <see cref="FolderConfig.FolderThemeId"/> 覆盖，
    /// 否则跟随全局当前主题。主题被删除后返回全局主题作为兜底。</summary>
    public ThemeConfig GetThemeForFolder(string? folderThemeId)
    {
        if (!string.IsNullOrEmpty(folderThemeId))
        {
            var t = Data.Themes.FirstOrDefault(x => x.Id == folderThemeId);
            if (t != null) return t;
        }
        return GetCurrentTheme();
    }

    /// <summary>某个主题是否"正在被使用"（作为全局当前主题，或被任一文件夹引用）；编辑这类主题时才需要通知重绘。</summary>
    public bool IsThemeInUse(string id)
    {
        if (Data.CurrentThemeId == id) return true;
        return Data.Folders.Any(f => f.FolderThemeId == id);
    }

    /// <summary>
    /// 应用「全局主题」：把该主题设为全局当前主题，并<b>清空所有文件夹的单独外观覆盖</b>，
    /// 使全局主题对所有文件夹立即生效（覆盖此前任何单文件夹设置）。
    /// 单独设置某个文件夹外观时请使用 <see cref="FolderConfig.FolderThemeId"/>，不要调用本方法。
    /// </summary>
    public void SetGlobalTheme(string id)
    {
        Data.CurrentThemeId = id;
        foreach (var f in Data.Folders)
            f.FolderThemeId = null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 配置写失败不影响主流程 */ }
    }

    public void NotifyChanged() => SettingsChanged?.Invoke();
}
