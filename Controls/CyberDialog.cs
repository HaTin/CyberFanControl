using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CyberFanControl.Controls;

public class CyberDialog : Window
{
    public enum DialogType { Info, Warning, Confirm }

    public bool Result { get; private set; }

    private CyberDialog(string title, string message, DialogType type, bool showCancel)
    {
        Result = false;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Color accent = type switch
        {
            DialogType.Warning => Color.FromRgb(255, 170, 0),
            DialogType.Confirm => Color.FromRgb(255, 0, 68),
            _ => Color.FromRgb(0, 240, 255)
        };

        string icon = type switch
        {
            DialogType.Warning => "⚠",
            DialogType.Confirm => "⚡",
            _ => "ℹ"
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 21, 26)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20),
            MaxWidth = 400,
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 15, ShadowDepth = 0, Opacity = 0.4 }
        };

        var stack = new StackPanel();

        // Title
        var titleBlock = new TextBlock
        {
            Text = $"{icon}  {title}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
            Margin = new Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(titleBlock);

        // Message
        var msgBlock = new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            LineHeight = 18
        };
        stack.Children.Add(msgBlock);

        // Buttons
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        if (showCancel)
        {
            var cancelBtn = CreateButton("取消", Color.FromRgb(102, 102, 128), Color.FromRgb(40, 40, 50));
            cancelBtn.Click += (s, e) => { Result = false; Close(); };
            btnPanel.Children.Add(cancelBtn);
        }

        var okText = showCancel ? "确认修改" : "确定";
        var okBtn = CreateButton(okText, accent, Color.FromRgb(13, 17, 23));
        okBtn.Click += (s, e) => { Result = true; Close(); };
        btnPanel.Children.Add(okBtn);

        stack.Children.Add(btnPanel);
        border.Child = stack;
        Content = border;

        MouseLeftButtonDown += (s, e) => DragMove();
    }

    private static Button CreateButton(string text, Color border, Color bg)
    {
        var btn = new Button
        {
            Content = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(border),
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        return btn;
    }

    // ============ Public Static Methods ============

    public static void ShowInfo(Window owner, string title, string message)
    {
        var dlg = new CyberDialog(title, message, DialogType.Info, false) { Owner = owner };
        dlg.ShowDialog();
    }

    public static void ShowWarning(Window owner, string title, string message)
    {
        var dlg = new CyberDialog(title, message, DialogType.Warning, false) { Owner = owner };
        dlg.ShowDialog();
    }

    public static bool ShowConfirm(Window owner, string title, string message)
    {
        var dlg = new CyberDialog(title, message, DialogType.Confirm, true) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    public static bool ShowGpuConfirm(Window owner, string action, string details)
    {
        string msg = $"即将执行: {action}\n\n{details}\n\n" +
                     "⚠ 错误的参数可能导致:\n" +
                     "  • 显示异常或黑屏\n" +
                     "  • 系统不稳定\n" +
                     "  • 需要重启恢复\n\n" +
                     "确认修改？";
        return ShowConfirm(owner, "GPU 硬件设置", msg);
    }
}
