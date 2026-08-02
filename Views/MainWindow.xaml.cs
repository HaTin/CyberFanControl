using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Controls;
using CyberFanControl.Services;
using CyberFanControl.Models;
using CyberFanControl.Controls;
using Microsoft.Win32;

namespace CyberFanControl.Views;

public partial class MainWindow : Window
{
    private readonly HardwareService _hw;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _animTimer;

    private double _cpuAngle, _gpuAngle;
    private double _cpuSpeed, _gpuSpeed;

    private List<TemperaturePoint> _cpuCurve;
    private List<TemperaturePoint> _gpuCurve;
    private bool _showCpuCurve = true;
    private TemperaturePoint? _dragged;
    private bool _dragging;

    private static readonly Color Cyan = Color.FromRgb(0, 240, 255);
    private static readonly Color Magenta = Color.FromRgb(255, 0, 170);
    private static readonly Color Green = Color.FromRgb(0, 255, 136);
    private static readonly Color Red = Color.FromRgb(255, 0, 51);
    private static readonly Color Orange = Color.FromRgb(255, 170, 0);
    private static readonly Color Blue = Color.FromRgb(0, 191, 255);

    // Tray icon
    private TrayIcon? _trayIcon;
    private bool _forceExit;
    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
    private const uint WM_TRAYICON = 0x0400 + 1;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private const string AutorunRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutorunName = "CyberFanControl";
    private const string AutorunTaskName = "CyberFanControl";

    public MainWindow()
    {
        InitializeComponent();
        _hw = new HardwareService();
        _cpuCurve = _hw.CpuCurve.ToList();
        _gpuCurve = _hw.GpuCurve.ToList();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += (s, e) => UpdateData();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += (s, e) => AnimateFans();

        Loaded += OnLoaded;
        SizeChanged += (s, e) => { UpdateFanSize(); DrawCurve(); };
    }

    #region Tray Icon

    private void InitTrayIcon()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _trayIcon = new TrayIcon(hwnd, WM_TRAYICON);
        _trayIcon.DoubleClick += ShowWindow;
        _trayIcon.OnShow += ShowWindow;
        _trayIcon.OnForceCool += ToggleForceCool;
        _trayIcon.OnExit += ExitApp;

        // 设置窗口图标为风扇图标
        var hIcon = TrayIcon.CreateFanIcon(32);
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        DestroyIcon(hIcon);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _animTimer.Start(); // 恢复风扇动画
    }

    private void ToggleForceCool()
    {
        if (!_hw.IsInitialized)
        {
            _trayIcon?.ShowBalloon("CYBER FAN CONTROL", "硬件初始化失败，无法控制风扇");
            return;
        }
        if (chkTakeOver.IsChecked != true)
            chkTakeOver.IsChecked = true;
        chkForce.IsChecked = !(chkForce.IsChecked == true);
        SaveConfig();
        _trayIcon?.ShowBalloon("CYBER FAN CONTROL",
            chkForce.IsChecked == true ? "最大风冷已开启" : "最大风冷已关闭");
    }

    private void ExitApp()
    {
        _forceExit = true;
        Close();
    }

    // DWM 圆角 + 去阴影
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ShowWindowRequested += ShowWindow;

        var source = PresentationSource.FromVisual(this)
            as System.Windows.Interop.HwndSource;
        source?.AddHook(WndProc);

        // 去除窗口阴影
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int val = 1; // DWMNCRP_DISABLED
        DwmSetWindowAttribute(hwnd, 2, ref val, 4); // DWMWA_NCRENDERING_POLICY

        // Windows 11 圆角
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref corner, 4); // DWMWA_WINDOW_CORNER_PREFERENCE

        // Init tray after window handle is available
        InitTrayIcon();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == WM_TASKBARCREATED)
        {
            // Explorer restarted, re-create tray icon
            _trayIcon?.Dispose();
            InitTrayIcon();
            handled = true;
        }
        else if ((uint)msg == WM_TRAYICON)
        {
            _trayIcon?.HandleMessage((uint)msg, lParam);
            handled = true;
        }
        else if ((uint)msg == WM_POWERBROADCAST)
        {
            int powerEvent = wParam.ToInt32();
            if (powerEvent == PBT_APMRESUMEAUTOMATIC || powerEvent == PBT_APMRESUMESUSPEND)
            {
                _hw.OnSystemResume();
                UpdateData();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    #endregion

    #region Auto Start

    private static bool IsAutorunSet()
    {
        try
        {
            var (code, _) = RunSchTasks("/Query", "/TN", AutorunTaskName, "/FO", "LIST");
            return code == 0;
        }
        catch { return false; }
    }

    private static bool SetAutorun(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exePath.Length == 0 || !File.Exists(exePath))
                {
                    MessageBox.Show("无法获取程序路径，自启动设置失败。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string xmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CyberFanControl_autorun.xml");
                File.WriteAllText(xmlPath, BuildAutorunTaskXml(exePath), Encoding.Unicode);
                try
                {
                    var (code, output) = RunSchTasks("/Create", "/F", "/XML", xmlPath, "/TN", AutorunTaskName);
                    if (code != 0)
                    {
                        MessageBox.Show($"计划任务创建失败（错误码 {code}）。\n请确认程序以管理员身份运行。\n\n{output.Trim()}",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                finally
                {
                    try { File.Delete(xmlPath); } catch { }
                }
            }
            else
            {
                var (code, _) = RunSchTasks("/Delete", "/F", "/TN", AutorunTaskName);
                if (code != 0 && IsAutorunSet())
                {
                    MessageBox.Show("计划任务删除失败，请确认程序以管理员身份运行。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            RemoveLegacyRunEntry();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"自启动设置失败：{ex.Message}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static (int ExitCode, string Output) RunSchTasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        if (p == null) return (-1, "");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10000))
        {
            try { p.Kill(); } catch { }
            return (-1, "");
        }
        return (p.ExitCode, outTask.Result + errTask.Result);
    }

    private static string BuildAutorunTaskXml(string exePath)
    {
        string command = System.Security.SecurityElement.Escape(exePath);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Date>{DateTime.Now:yyyy-MM-ddTHH:mm:ss}</Date>
    <Author>CyberFanControl</Author>
    <URI>\CyberFanControl</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>StopExisting</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>--silent</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutorunRegKey, true);
            key?.DeleteValue(AutorunName, false);
        }
        catch { }
    }

    private void ApplyInterval()
    {
        // 限制 1~60 秒：下限保证控制响应，上限防止控制循环失活
        int seconds = int.TryParse(txtInterval.Text, out int i) ? Math.Clamp(i, 1, 60) : 2;
        _updateTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private void Autorun_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressConfigChanged) return;
        bool enable = chkAutorun.IsChecked == true;
        if (!SetAutorun(enable))
        {
            _suppressConfigChanged = true;
            chkAutorun.IsChecked = !enable;
            _suppressConfigChanged = false;
        }
    }

    #endregion

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        txtStatus.Text = _hw.InitStatus.Replace("\n", " | ");
        statusDot.Fill = new SolidColorBrush(_hw.IsInitialized ? Green : Red);

        // Check if started with --silent flag (auto-start mode)
        var args = Environment.GetCommandLineArgs();
        bool silentStart = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silentStart)
        {
            Hide();
        }

        // Load saved config
        LoadConfigToUI();
        ApplyInterval();

        // Check autorun state
        _suppressConfigChanged = true;
        chkAutorun.IsChecked = false; // 先置 false，避免界面长时间显示不确定状态
        _suppressConfigChanged = false;
        // schtasks 查询可能阻塞数秒，放到后台线程，完成后回到 UI 线程更新
        _ = Task.Run(() =>
        {
            bool set = IsAutorunSet();
            Dispatcher.BeginInvoke(() =>
            {
                _suppressConfigChanged = true;
                chkAutorun.IsChecked = set;
                _suppressConfigChanged = false;
            });
        });
        RemoveLegacyRunEntry();

        UpdateFanSize();
        _updateTimer.Start();
        if (!silentStart) _animTimer.Start(); // 后台启动时不启动动画
        UpdateData();
        DrawCurve();
        ((App)Application.Current).ConsumePendingShowWindowRequest();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit)
        {
            e.Cancel = true;
            Hide();
            _animTimer.Stop(); // 窗口隐藏时停止风扇动画，节省 GPU 渲染
            _trayIcon?.ShowBalloon("CYBER FAN CONTROL", "程序已最小化到系统托盘");
            return;
        }

        App.ShowWindowRequested -= ShowWindow;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _updateTimer.Stop();
        _animTimer.Stop();
        _hw.Dispose();
        base.OnClosing(e);
    }

    private void LoadConfigToUI()
    {
        var cfg = _hw.LoadConfig();
        if (cfg == null) return;

        _suppressConfigChanged = true;

        chkTakeOver.IsChecked = cfg.TakeOver;
        chkLinear.IsChecked = cfg.Linear;
        chkSoft.IsChecked = cfg.SoftControl;
        chkForce.IsChecked = cfg.ForceCool;

        txtInterval.Text = cfg.Interval.ToString();
        txtTransition.Text = cfg.TransitionTemp.ToString();
        txtMinDuty.Text = cfg.MinFanDuty.ToString();

        chkGpuFreq.IsChecked = cfg.LockGpuFreq;
        txtGpuFreq.Text = cfg.GpuFreqLimit.ToString();
        chkMemOffset.IsChecked = cfg.LockMemOverclock;
        txtMemOffset.Text = cfg.GpuMemOffset.ToString();

        _suppressConfigChanged = false;

        if (cfg.CpuCurve != null && cfg.CpuCurve.Count > 0)
            _cpuCurve = cfg.CpuCurve;
        if (cfg.GpuCurve != null && cfg.GpuCurve.Count > 0)
            _gpuCurve = cfg.GpuCurve;

        _hw.SetCpuCurve(_cpuCurve.ToList());
        _hw.SetGpuCurve(_gpuCurve.ToList());

        ApplyGpuSettings();
        var initStatus = _hw.InitStatus.Replace("\n", " | ");
        txtStatus.Text = string.IsNullOrWhiteSpace(initStatus) ? "配置已加载" : $"{initStatus} | 配置已加载";
    }

    private void UpdateFanSize()
    {
        double sz = 60;
        CpuFanCanvas.Width = sz; CpuFanCanvas.Height = sz;
        GpuFanCanvas.Width = sz; GpuFanCanvas.Height = sz;
        DrawFan(CpuFanCanvas, _cpuAngle, Cyan);
        DrawFan(GpuFanCanvas, _gpuAngle, Magenta);
    }

    #region Fan Drawing

    private void DrawFan(Canvas c, double angle, Color color)
    {
        c.Children.Clear();
        double cx = c.Width / 2, cy = c.Height / 2, r = Math.Min(cx, cy) - 4;

        c.Children.Add(new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Stroke = new SolidColorBrush(color), StrokeThickness = 1.5,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
        });
        Canvas.SetLeft(c.Children[^1], cx - r);
        Canvas.SetTop(c.Children[^1], cy - r);

        c.Children.Add(new Ellipse
        {
            Width = r * 0.5, Height = r * 0.5,
            Stroke = new SolidColorBrush(Color.FromArgb(180, color.R, color.G, color.B)), StrokeThickness = 1
        });
        Canvas.SetLeft(c.Children[^1], cx - r * 0.25);
        Canvas.SetTop(c.Children[^1], cy - r * 0.25);

        for (int i = 0; i < 7; i++)
        {
            double a = (angle + i * 360.0 / 7) * Math.PI / 180;
            double ir = r * 0.18, or = r * 0.82, bw = 0.22;
            var blade = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color), StrokeThickness = 0.3
            };
            blade.Points.Add(new Point(cx + ir * Math.Cos(a - bw), cy + ir * Math.Sin(a - bw)));
            blade.Points.Add(new Point(cx + or * Math.Cos(a - bw * 0.5), cy + or * Math.Sin(a - bw * 0.5)));
            blade.Points.Add(new Point(cx + or * Math.Cos(a + bw * 0.5), cy + or * Math.Sin(a + bw * 0.5)));
            blade.Points.Add(new Point(cx + ir * Math.Cos(a + bw), cy + ir * Math.Sin(a + bw)));
            c.Children.Add(blade);
        }

        c.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Fill = new SolidColorBrush(color),
            Effect = new DropShadowEffect { Color = color, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 }
        });
        Canvas.SetLeft(c.Children[^1], cx - 3);
        Canvas.SetTop(c.Children[^1], cy - 3);
    }

    #endregion

    #region Animation & Data

    private void AnimateFans()
    {
        _cpuAngle = (_cpuAngle + _cpuSpeed) % 360;
        _gpuAngle = (_gpuAngle + _gpuSpeed) % 360;
        DrawFan(CpuFanCanvas, _cpuAngle, Cyan);
        DrawFan(GpuFanCanvas, _gpuAngle, Magenta);
    }

    private void UpdateData()
    {
        try
        {
            var s = _hw.GetStatus();
            bool visible = IsVisible;

            // UI 更新：仅窗口可见时执行
            if (visible)
            {
                txtCpuTemp.Text = s.CpuTemp.ToString();
                txtGpuTemp.Text = s.GpuTempValid ? s.GpuTemp.ToString() : "--";
                txtCpuRpm.Text = s.CpuFanRpm.ToString();
                txtGpuRpm.Text = s.GpuFanRpm.ToString();
                txtCpuDuty.Text = s.CpuDuty.ToString();
                txtCpuTargetDuty.Text = _hw.CpuTargetDuty.ToString();
                txtGpuDuty.Text = s.GpuTempValid ? s.GpuDuty.ToString() : "--";
                txtGpuTargetDuty.Text = _hw.GpuTargetDuty.ToString();

                TempColor(txtCpuTemp, s.CpuTemp);
                if (s.GpuTempValid)
                    TempColor(txtGpuTemp, s.GpuTemp);
                else
                    txtGpuTemp.Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 128));

                txtGpuCore.Text = $"{s.GpuCoreClock} MHz";
                txtGpuMem.Text = $"{s.GpuMemClock} MHz";
                txtGpuUsage.Text = $"{s.GpuUsage} %";
                if (!string.IsNullOrEmpty(s.GpuName)) txtGpuName.Text = s.GpuName;

                _cpuSpeed = s.CpuDuty * 0.4;
                if (s.GpuTempValid) _gpuSpeed = s.GpuDuty * 0.4;
            }

            // 风扇控制 + 托盘提示：始终执行
            if (chkTakeOver.IsChecked == true)
                _hw.UpdateTargetDuty(s.CpuTemp, s.GpuTempValid ? s.GpuTemp : (int?)null, s.CpuDuty, s.GpuDuty);

            string gpuTip = s.GpuTempValid ? $"{s.GpuTemp}°C {s.GpuDuty}%" : "--";
            _trayIcon?.UpdateTooltip($"CPU:{s.CpuTemp}°C {s.CpuDuty}% | GPU:{gpuTip}");
        }
        catch
        {
            if (IsVisible)
                foreach (var t in new[] { txtCpuTemp, txtGpuTemp, txtCpuRpm, txtGpuRpm, txtCpuDuty, txtGpuDuty })
                    t.Text = "--";
        }
    }

    private void TempColor(TextBlock t, int temp)
    {
        t.Foreground = new SolidColorBrush(temp < 50 ? Blue : temp < 65 ? Green : temp < 80 ? Orange : Red);
    }

    #endregion

    #region Curve Editor

    private void DrawCurve()
    {
        CurveCanvas.Children.Clear();
        var points = _showCpuCurve ? _cpuCurve : _gpuCurve;
        Color color = _showCpuCurve ? Cyan : Magenta;
        if (points.Count < 2) return;

        double w = CurveCanvas.ActualWidth > 0 ? CurveCanvas.ActualWidth : 300;
        double h = CurveCanvas.ActualHeight > 0 ? CurveCanvas.ActualHeight : 200;
        double mx = 30, my = 15;

        var gb = new SolidColorBrush(Color.FromArgb(25, 0, 240, 255));
        for (int t = 40; t <= 95; t += 5)
        {
            double x = mx + (t - 40) / 55.0 * (w - mx * 2);
            CurveCanvas.Children.Add(new Line { X1 = x, Y1 = my, X2 = x, Y2 = h - my, Stroke = gb, StrokeThickness = 0.5 });
            var lb = new TextBlock { Text = $"{t}°", FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 100)), FontFamily = new FontFamily("Consolas") };
            Canvas.SetLeft(lb, x - 10); Canvas.SetTop(lb, h - my + 2);
            CurveCanvas.Children.Add(lb);
        }
        for (int d = 0; d <= 100; d += 20)
        {
            double y = h - my - (d / 100.0) * (h - my * 2);
            CurveCanvas.Children.Add(new Line { X1 = mx, Y1 = y, X2 = w - mx, Y2 = y, Stroke = gb, StrokeThickness = 0.5 });
            var lb = new TextBlock { Text = $"{d}%", FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 100)), FontFamily = new FontFamily("Consolas") };
            Canvas.SetLeft(lb, 2); Canvas.SetTop(lb, y - 6);
            CurveCanvas.Children.Add(lb);
        }

        var sorted = points.OrderBy(p => p.Temperature).ToList();
        var pts = sorted.Select(p => new Point(
            mx + (p.Temperature - 40) / 55.0 * (w - mx * 2),
            h - my - (p.DutyPercent / 100.0) * (h - my * 2)
        )).ToList();

        var fig = new PathFigure { StartPoint = pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var p0 = pts[i - 1];
            var p1 = pts[i];
            double dx = p1.X - p0.X;
            double dy = p1.Y - p0.Y;
            if (dx == 0)
            {
                fig.Segments.Add(new LineSegment(p1, true));
                continue;
            }
            double slope = dy / dx;

            double cp1x = p0.X + dx / 3.0;
            double cp1y = p0.Y + slope * dx / 3.0;
            double cp2x = p0.X + dx * 2.0 / 3.0;
            double cp2y = p0.Y + slope * dx * 2.0 / 3.0;

            double minY = Math.Min(p0.Y, p1.Y);
            double maxY = Math.Max(p0.Y, p1.Y);
            cp1y = Math.Clamp(cp1y, minY, maxY);
            cp2y = Math.Clamp(cp2y, minY, maxY);

            fig.Segments.Add(new BezierSegment(
                new Point(cp1x, cp1y),
                new Point(cp2x, cp2y),
                p1, true));
        }
        CurveCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry { Figures = { fig } },
            Stroke = new SolidColorBrush(color), StrokeThickness = 2,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
        });

        foreach (var p in sorted)
        {
            double x = mx + (p.Temperature - 40) / 55.0 * (w - mx * 2);
            double y = h - my - (p.DutyPercent / 100.0) * (h - my * 2);
            var el = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)),
                StrokeThickness = 1, Tag = p,
                Effect = new DropShadowEffect { Color = color, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.5 }
            };
            Canvas.SetLeft(el, x - 4); Canvas.SetTop(el, y - 4);
            CurveCanvas.Children.Add(el);
        }
    }

    private Point ToData(Point p, double w, double h)
    {
        double mx = 30, my = 15;
        return new Point(
            Math.Clamp(40 + (p.X - mx) / (w - mx * 2) * 55, 40, 95),
            Math.Clamp((1 - (p.Y - my) / (h - my * 2)) * 100, 0, 100)
        );
    }

    private void CurveCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(CurveCanvas);
        double w = CurveCanvas.ActualWidth, h = CurveCanvas.ActualHeight;
        var curve = _showCpuCurve ? _cpuCurve : _gpuCurve;
        double mx = 30, my = 15;

        foreach (var child in CurveCanvas.Children)
        {
            if (child is Ellipse el && el.Tag is TemperaturePoint pt)
            {
                double x = mx + (pt.Temperature - 40) / 55.0 * (w - mx * 2);
                double y = h - my - (pt.DutyPercent / 100.0) * (h - my * 2);
                if (Math.Abs(pos.X - x) < 10 && Math.Abs(pos.Y - y) < 10)
                {
                    _dragged = pt; _dragging = true; CurveCanvas.CaptureMouse(); return;
                }
            }
        }

        var dp = ToData(pos, w, h);
        // 加点约束：最小温度间距 1°C，总点数上限 30，避免曲线过密导致 UI 卡顿与查找变慢
        const double MinTempGap = 1.0;
        const int MaxPoints = 30;
        var existing = curve.FirstOrDefault(p => Math.Abs(p.Temperature - dp.X) < MinTempGap);
        if (existing != null)
        {
            existing.DutyPercent = dp.Y;
        }
        else if (curve.Count >= MaxPoints)
        {
            // 超过上限：替换离点击点温度最近的一个点，而非无限堆叠
            var nearest = curve.OrderBy(p => Math.Abs(p.Temperature - dp.X)).First();
            nearest.Temperature = dp.X;
            nearest.DutyPercent = dp.Y;
        }
        else
        {
            curve.Add(new TemperaturePoint(dp.X, dp.Y));
        }
        SyncCurves();
        DrawCurve();
    }

    private void CurveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragged == null) return;
        var pos = e.GetPosition(CurveCanvas);
        var dp = ToData(pos, CurveCanvas.ActualWidth, CurveCanvas.ActualHeight);
        var curve = _showCpuCurve ? _cpuCurve : _gpuCurve;
        var sorted = curve.OrderBy(p => p.Temperature).ToList();
        int idx = sorted.IndexOf(_dragged);
        double minT = idx > 0 ? sorted[idx - 1].Temperature + 1 : 40;
        double maxT = idx < sorted.Count - 1 ? sorted[idx + 1].Temperature - 1 : 95;
        _dragged.Temperature = Math.Clamp(dp.X, minT, maxT);
        _dragged.DutyPercent = Math.Clamp(dp.Y, 0, 100);
        SyncCurves();
        DrawCurve();
    }

    private void CurveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false; _dragged = null; CurveCanvas.ReleaseMouseCapture();
    }

    private void CurveCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(CurveCanvas);
        double w = CurveCanvas.ActualWidth, h = CurveCanvas.ActualHeight;
        var curve = _showCpuCurve ? _cpuCurve : _gpuCurve;
        double mx = 30, my = 15;

        // 命中半径 10 像素内的点删除；至少保留 2 个点（CalculateDuty 要求 ≥2）
        TemperaturePoint? hit = null;
        foreach (var p in curve)
        {
            double x = mx + (p.Temperature - 40) / 55.0 * (w - mx * 2);
            double y = h - my - (p.DutyPercent / 100.0) * (h - my * 2);
            if (Math.Abs(pos.X - x) < 10 && Math.Abs(pos.Y - y) < 10) { hit = p; break; }
        }
        if (hit != null && curve.Count > 2)
        {
            curve.Remove(hit);
            SyncCurves();
            DrawCurve();
        }
    }

    private void SyncCurves()
    {
        if (_showCpuCurve) _hw.SetCpuCurve(_cpuCurve.ToList());
        else _hw.SetGpuCurve(_gpuCurve.ToList());
        SaveConfig(); // 拖动点后自动保存，防止曲线修改丢失
    }

    private void TabCpu_Click(object sender, MouseButtonEventArgs e)
    {
        _showCpuCurve = true;
        tabCpu.Background = new SolidColorBrush(Cyan);
        tabCpu.BorderBrush = new SolidColorBrush(Cyan);
        tabCpuText.Foreground = Brushes.Black;
        tabGpu.Background = new SolidColorBrush(Color.FromRgb(26, 26, 46));
        tabGpu.BorderBrush = new SolidColorBrush(Magenta);
        tabGpuText.Foreground = new SolidColorBrush(Magenta);
        DrawCurve();
    }

    private void TabGpu_Click(object sender, MouseButtonEventArgs e)
    {
        _showCpuCurve = false;
        tabGpu.Background = new SolidColorBrush(Magenta);
        tabGpu.BorderBrush = new SolidColorBrush(Magenta);
        tabGpuText.Foreground = Brushes.Black;
        tabCpu.Background = new SolidColorBrush(Color.FromRgb(26, 26, 46));
        tabCpu.BorderBrush = new SolidColorBrush(Cyan);
        tabCpuText.Foreground = new SolidColorBrush(Cyan);
        DrawCurve();
    }

    #endregion

    #region Window Controls

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void MinBtn(object s, RoutedEventArgs e) { Hide(); _animTimer.Stop(); }
    private void CloseBtn(object s, RoutedEventArgs e) => Close();

    #endregion

    #region Config & Buttons

    private void Opt_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressConfigChanged) return;
        if (chkForce.IsChecked == true && chkTakeOver.IsChecked != true)
            chkTakeOver.IsChecked = true;
        SaveConfig();
        if (chkTakeOver.IsChecked != true) _hw.ResetAllFans();
    }

    private bool _suppressConfigChanged;

    private void GpuCtrl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hw.IsInitialized || _suppressConfigChanged) return;

        var cfg = _hw.Config;
        bool isNewCheck = (chkGpuFreq.IsChecked == true && !cfg.LockGpuFreq)
                       || (chkMemOffset.IsChecked == true && !cfg.LockMemOverclock);

        if (isNewCheck)
        {
            var details = new List<string>();
            if (chkGpuFreq.IsChecked == true && int.TryParse(txtGpuFreq.Text, out int freq) && freq > 0)
                details.Add($"  限制频率: {freq} MHz");
            if (chkMemOffset.IsChecked == true && int.TryParse(txtMemOffset.Text, out int mem) && mem != 0)
                details.Add($"  显存偏移: {mem} MHz");

            if (!CyberDialog.ShowGpuConfirm(this, "应用 GPU 硬件设置", string.Join("\n", details)))
            {
                _suppressConfigChanged = true;
                chkGpuFreq.IsChecked = cfg.LockGpuFreq;
                txtGpuFreq.Text = cfg.GpuFreqLimit.ToString();
                chkMemOffset.IsChecked = cfg.LockMemOverclock;
                txtMemOffset.Text = cfg.GpuMemOffset.ToString();
                _suppressConfigChanged = false;
                return;
            }
        }

        SaveConfig();
        bool ok = ApplyGpuSettings();
        txtStatus.Text = ok ? "GPU 设置已应用" : "GPU 设置应用失败，详见日志";
    }

    private bool ApplyGpuSettings()
    {
        return _hw.ApplyGpuSettings();
    }

    private void SaveBtn(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        ApplyGpuSettings();
        txtStatus.Text = "配置已保存并应用";
        CyberDialog.ShowInfo(this, "保存成功", "所有配置已保存到本地文件。\n下次启动将自动加载。");
    }

    private void ResetBtn(object sender, RoutedEventArgs e)
    {
        if (!CyberDialog.ShowConfirm(this, "重置确认", "确定要重置所有设置吗？\n\n包括:\n  • 所有勾选项\n  • CPU / GPU 曲线\n  • GPU 限频 / 显存偏移\n\n此操作不可撤销。")) return;
        _hw.ResetToDefault();
        _cpuCurve = _hw.CpuCurve.ToList();
        _gpuCurve = _hw.GpuCurve.ToList();

        _suppressConfigChanged = true;
        chkTakeOver.IsChecked = chkLinear.IsChecked = chkSoft.IsChecked = chkForce.IsChecked = false;
        chkGpuFreq.IsChecked = chkMemOffset.IsChecked = false;
        txtGpuFreq.Text = txtMemOffset.Text = "0";
        txtInterval.Text = "2"; txtTransition.Text = "3"; txtMinDuty.Text = "18";
        _suppressConfigChanged = false;
        ApplyInterval();

        DrawCurve();
        SaveConfig();
        txtStatus.Text = "已重置";
    }

    private void SaveConfig()
    {
        var cfg = new ConfigProfile
        {
            Interval = Math.Clamp(int.TryParse(txtInterval.Text, out int i) ? i : 2, 1, 60),
            TransitionTemp = int.TryParse(txtTransition.Text, out int t) ? t : 3,
            MinFanDuty = Math.Clamp(int.TryParse(txtMinDuty.Text, out int md) ? md : 18, 1, 100),
            TakeOver = chkTakeOver.IsChecked == true,
            Linear = chkLinear.IsChecked == true,
            SoftControl = chkSoft.IsChecked == true,
            ForceCool = chkForce.IsChecked == true,
            LockGpuFreq = chkGpuFreq.IsChecked == true,
            GpuFreqLimit = int.TryParse(txtGpuFreq.Text, out int f) ? f : 0,
            LockMemOverclock = chkMemOffset.IsChecked == true,
            GpuMemOffset = int.TryParse(txtMemOffset.Text, out int m) ? m : 0,
            CpuCurve = _cpuCurve.ToList(),
            GpuCurve = _gpuCurve.ToList()
        };
        _hw.SaveConfig(cfg);
        ApplyInterval();
    }

    #endregion
}
