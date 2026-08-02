using System.IO;
using System.Text;
using System.Threading;

namespace CyberFanControl.Services;

/// <summary>
/// 轻量缓冲日志：定时落盘 + 文件大小上限滚动 + 级别过滤。
/// 避免每条日志开关文件句柄，并防止日志无限增长。
/// </summary>
internal sealed class FanLogger : IDisposable
{
    private readonly string _path;
    private readonly string _oldPath;
    private readonly long _maxSize;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private long _size;
    private readonly Timer _flushTimer;
    private readonly bool _debugEnabled;

    /// <param name="path">日志文件路径</param>
    /// <param name="maxSize">单文件大小上限，超出后滚动到 .old 重新开始</param>
    /// <param name="debugEnabled">是否记录 Debug 级别（高频轨迹日志）</param>
    public FanLogger(string path, long maxSize = 512 * 1024, bool debugEnabled = false)
    {
        _path = path;
        _oldPath = path + ".old";
        _maxSize = maxSize;
        _debugEnabled = debugEnabled;
        Open();
        // 每 5 秒落盘一次，兼顾持久性与性能
        _flushTimer = new Timer(_ => Flush(), null, 5000, 5000);
    }

    private void Open()
    {
        try
        {
            _size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            _writer = new StreamWriter(_path, append: true, new UTF8Encoding(false)) { AutoFlush = false };
        }
        catch { _writer = null; }
    }

    public void Info(string msg) => Write("I", msg);

    public void Debug(string msg)
    {
        if (_debugEnabled) Write("D", msg);
    }

    private void Write(string level, string msg)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss} {level}] {msg}");
                // 粗略估算增量（时间戳前缀约 16 字节 + 消息 + 换行）
                _size += msg.Length + 18;
                if (_size > _maxSize) Rotate();
            }
            catch { }
        }
    }

    private void Rotate()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            // 仅保留一份 .old：当前文件 → .old，再开新文件
            try { if (File.Exists(_oldPath)) File.Delete(_oldPath); } catch { }
            try { if (File.Exists(_path)) File.Move(_path, _oldPath); } catch { }
        }
        catch { }
        Open();
    }

    public void Flush()
    {
        lock (_lock) { try { _writer?.Flush(); } catch { } }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_lock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
