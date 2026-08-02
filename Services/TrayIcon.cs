using System.Drawing;
using System.Runtime.InteropServices;

namespace CyberFanControl.Services;

/// <summary>
/// Pure P/Invoke system tray icon - no WinForms dependency
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly uint _callbackMsg;
    private NOTIFYICONDATA _nid;
    private bool _added;
    private IntPtr _hIcon;
    private IntPtr _hwnd;

    public event Action? DoubleClick;
    public Action? OnShow;
    public Action? OnForceCool;
    public Action? OnExit;

    public TrayIcon(IntPtr hwnd, uint callbackMsg)
    {
        _hwnd = hwnd;
        _callbackMsg = callbackMsg;
        CreateIcon();
    }

    private void CreateIcon()
    {
        _hIcon = CreateFanIcon(16);

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = _callbackMsg,
            hIcon = _hIcon,
            szTip = "CYBER FAN CONTROL"
        };

        Shell_NotifyIcon(NIM_ADD, ref _nid);
        _added = true; // 即使失败也尝试在 Dispose 时清理
    }

    /// <summary>
    /// 创建风扇图标：圆形半透明背景 + 风扇叶片
    /// </summary>
    public static IntPtr CreateFanIcon(int size)
    {
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            float cx = size / 2f, cy = size / 2f, r = size / 2f - 1;
            var bg = System.Drawing.Color.FromArgb(180, 0x0D, 0x11, 0x17);
            var accent = System.Drawing.Color.FromArgb(0x00, 0xF0, 0xFF);

            // 圆形背景
            using (var bgBrush = new System.Drawing.SolidBrush(bg))
                g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);

            // 圆形边框
            using (var borderPen = new System.Drawing.Pen(accent, 1f))
                g.DrawEllipse(borderPen, 1, 1, size - 2, size - 2);

            // 风扇叶片
            using (var bladeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 0x00, 0xF0, 0xFF)))
            {
                float ir = r * 0.22f, or = r * 0.72f, bw = 0.55f;
                for (int i = 0; i < 5; i++)
                {
                    float a = (float)(i * 2 * Math.PI / 5);
                    var pts = new PointF[]
                    {
                        new(cx + (float)Math.Cos(a - bw) * ir, cy + (float)Math.Sin(a - bw) * ir),
                        new(cx + (float)Math.Cos(a - bw * 0.4f) * or, cy + (float)Math.Sin(a - bw * 0.4f) * or),
                        new(cx + (float)Math.Cos(a + bw * 0.4f) * or, cy + (float)Math.Sin(a + bw * 0.4f) * or),
                        new(cx + (float)Math.Cos(a + bw) * ir, cy + (float)Math.Sin(a + bw) * ir),
                    };
                    g.FillPolygon(bladeBrush, pts);
                }
            }

            // 中心圆
            float cr = r * 0.18f;
            using (var centerBrush = new System.Drawing.SolidBrush(accent))
                g.FillEllipse(centerBrush, cx - cr, cy - cr, cr * 2, cr * 2);
        }
        return bmp.GetHicon();
    }

    public void UpdateTooltip(string text)
    {
        if (text.Length > 127) text = text[..127];
        _nid.szTip = text;
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    public void ShowBalloon(string title, string text)
    {
        _nid.dwInfoFlags = 0;
        _nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        _nid.szInfo = text.Length > 255 ? text[..255] : text;
        _nid.uFlags = NIF_INFO;
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        _nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    }

    public void HandleMessage(uint msg, IntPtr lParam)
    {
        if (msg != _callbackMsg) return;

        switch (lParam.ToInt32())
        {
            case WM_LBUTTONDBLCLK:
                DoubleClick?.Invoke();
                break;
            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();

        AppendMenu(hMenu, MF_STRING, 1, "打开界面");
        AppendMenu(hMenu, MF_STRING, 2, "最大风冷");
        AppendMenu(hMenu, MF_SEPARATOR, 0, "");
        AppendMenu(hMenu, MF_STRING, 3, "退出程序");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(_hwnd);

        uint cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

        DestroyMenu(hMenu);

        // Win32 规范：菜单关闭后 PostMessage(WM_NULL) 确保 focus 转移，
        // 否则点击菜单外区域可能无法立即关闭
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (cmd)
        {
            case 1: OnShow?.Invoke(); break;
            case 2: OnForceCool?.Invoke(); break;
            case 3: OnExit?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    #region P/Invoke

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int WM_NULL = 0x0000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    #endregion
}
