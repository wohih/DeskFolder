using System.Windows.Media.Imaging;

namespace DeskFolder.Models;

/// <summary>一个已解析的快捷方式</summary>
public class ShortcutItem
{
    public string Name { get; set; } = "";
    public string LinkPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public BitmapSource? Icon { get; set; }
}
