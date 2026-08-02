using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CyberFanControl;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\CyberFanControl.SingleInstance";
    private const string ShowWindowEventName = @"Local\CyberFanControl.ShowWindow";

    public static event Action? ShowWindowRequested;

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showWindowEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            RequestShowWindow();
            Shutdown();
            return;
        }
        _ownsMutex = true;

        StartShowWindowListener();

        base.OnStartup(e);

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"发生未处理的异常: {args.Exception.Message}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    private void StartShowWindowListener()
    {
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var listener = new Thread(ShowWindowListenerLoop)
        {
            IsBackground = true,
            Name = "CyberFanControl.ShowWindowListener"
        };
        listener.Start();
    }

    private void ShowWindowListenerLoop()
    {
        while (true)
        {
            try
            {
                _showWindowEvent?.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_showWindowEvent == null) return;

            try
            {
                Dispatcher.Invoke(() => ShowWindowRequested?.Invoke());
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Signal delivery must never take down the app; keep listening.
            }
        }
    }

    internal void ConsumePendingShowWindowRequest()
    {
        try
        {
            if (_showWindowEvent?.WaitOne(0) == true)
                ShowWindowRequested?.Invoke();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void RequestShowWindow()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
            signal.Set();
        }
        catch
        {
            MessageBox.Show("CyberFanControl 已在运行，但无法激活现有窗口。\n请先通过托盘图标退出旧进程。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        base.OnExit(e);
    }
}
