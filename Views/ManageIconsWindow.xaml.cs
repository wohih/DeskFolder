using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskFolder.Models;
using DeskFolder.Services;

namespace DeskFolder.Views;

/// <summary>「删除图标」对话框：列出文件夹内全部图标，勾选后从文件夹移除（不删除磁盘文件）。</summary>
public partial class ManageIconsWindow : Window
{
    private readonly FolderConfig _config;

    public ManageIconsWindow(FolderConfig config)
    {
        _config = config;
        InitializeComponent();
        BuildList();
    }

    private void BuildList()
    {
        if (_config.Shortcuts.Count == 0)
        {
            IconList.Children.Add(new TextBlock
            {
                Text = "（该文件夹暂无图标）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99))
            });
            return;
        }
        foreach (var path in _config.Shortcuts)
        {
            var item = ShortcutService.Resolve(path, loadIcon: true);
            var name = item?.Name ?? Path.GetFileNameWithoutExtension(path);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 8)
            };

            // 图标（真实图标，圆角容器）
            if (item?.Icon != null)
            {
                var iconBorder = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ClipToBounds = true
                };
                var img = new Image
                {
                    Source = item.Icon,
                    Width = 36,
                    Height = 36,
                    Stretch = Stretch.Uniform
                };
                iconBorder.Child = img;
                row.Children.Add(iconBorder);
            }

            row.Children.Add(new CheckBox
            {
                Content = name,
                Tag = path,
                ToolTip = path,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            });

            IconList.Children.Add(row);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = AllCheckBoxes()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Tag as string)
            .Where(p => p != null)
            .Cast<string>()
            .ToList();
        if (toRemove.Count == 0)
        {
            DialogResult = false;
            return;
        }
        var result = MessageBox.Show(
            $"确定要从文件夹中移除选中的 {toRemove.Count} 个图标吗？\n（仅移出本文件夹，磁盘上的快捷方式不会被删除）",
            "删除图标",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            DialogResult = false;
            return;
        }
        foreach (var p in toRemove)
            _config.Shortcuts.Remove(p);
        App.Settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>递归收集 IconList 内的所有CheckBox（图标行外层套了 StackPanel）。</summary>
    private IEnumerable<CheckBox> AllCheckBoxes()
    {
        foreach (var child in IconList.Children)
        {
            if (child is CheckBox cb)
                yield return cb;
            else if (child is Panel panel)
            {
                foreach (var inner in panel.Children.OfType<CheckBox>())
                    yield return inner;
            }
        }
    }
}
