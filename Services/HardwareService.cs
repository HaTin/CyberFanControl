using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CyberFanControl.Models;

namespace CyberFanControl.Services;

public class HardwareService : IDisposable
{
    private bool _ecInitialized;
    private bool _gpuInitialized;
    private readonly object _lock = new();
    private ConfigProfile _config;
    private List<TemperaturePoint> _cpuCurve;
    private List<TemperaturePoint> _gpuCurve;

    private int[] _targetDuty = new int[2];
    private int[] _currentDuty = new int[2];
    private Thread? _softControlThread;
    private volatile bool _running = true;
    private volatile bool _forceCoolActive;
    private int _lastCpuTemp = int.MinValue;
    private int _lastGpuTemp = int.MinValue;
    private bool _cpuControlActive;
    private bool _gpuControlActive;
    private volatile bool _gpuTempValid; // 最近一次 UpdateTargetDuty 时 GPU 温度是否有效
    private int _softReassertCounter;
    private int _lastSentCpuDuty = -1;
    private int _lastSentGpuDuty = -1;

    public string InitStatus { get; private set; } = "未初始化";
    public bool IsInitialized => _ecInitialized;

    public HardwareService()
    {
        _config = new ConfigProfile();
        _cpuCurve = DefaultCurve();
        _gpuCurve = DefaultCurve();
        Initialize();
    }

    private static List<TemperaturePoint> DefaultCurve() => new()
    {
        new(45, 18), new(50, 20), new(55, 35), new(60, 45), new(65, 55),
        new(70, 65), new(75, 75), new(80, 85), new(85, 95), new(90, 100)
    };

    private void Initialize()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string clevoPath = Path.Combine(exeDir, "ClevoEcInfo.dll");
        string nvPath = Path.Combine(exeDir, "NVGPU_DLL.dll");

        if (!File.Exists(clevoPath))
        {
            InitStatus = $"找不到 ClevoEcInfo.dll\n{clevoPath}";
            return;
        }

        try
        {
            int r = NativeInterop.InitIo();
            if (r != 1) { InitStatus = $"InitIo 返回 {r}，需要 NTPortDrv 驱动"; return; }
            _ecInitialized = true;
            InitStatus = "EC 初始化成功";

            try
            {
                int fanCount = NativeInterop.GetFanCount();
                Log($"Fan probe: count={fanCount}");
                for (int i = 1; i <= Math.Max(3, fanCount); i++)
                {
                    var d = NativeInterop.GetTempFanDuty(i);
                    Log($"  Fan {i}: temp={d.Remote} dutyRaw={d.FanDuty}");
                }
            }
            catch (Exception ex) { Log($"Fan probe failed: {ex.Message}"); }
        }
        catch (Exception ex) { InitStatus = $"EC 初始化失败: {ex.Message}"; return; }

        // GPU
        if (File.Exists(nvPath))
        {
            try
            {
                int gr = NativeInterop.InitGPU_API();
                if (gr == 0)
                {
                    NativeInterop.Set_GPU_Number(0);
                    _gpuInitialized = true;
                    InitStatus += "\nGPU 初始化成功";
                }
                else InitStatus += $"\nGPU 初始化失败 ({gr})";
            }
            catch (Exception ex) { InitStatus += $"\nGPU: {ex.Message}"; }
        }
        else InitStatus += "\nNVGPU_DLL.dll 未找到";

        _softControlThread = new Thread(SoftControlLoop) { IsBackground = true };
        _softControlThread.Start();
    }

    public FanStatus GetStatus()
    {
        var s = new FanStatus();
        if (!_ecInitialized) return s;

        lock (_lock)
        {
            try
            {
                var cd = NativeInterop.GetTempFanDuty(1);
                s.CpuTemp = cd.Remote;
                s.CpuDuty = cd.FanDuty * 100 / 255;
            }
            catch { }

            try
            {
                var gd = NativeInterop.GetTempFanDuty(2);
                s.GpuTemp = gd.Remote;
                s.GpuDuty = gd.FanDuty * 100 / 255;
                s.GpuTempValid = true;
            }
            catch { }

            try { int r = NativeInterop.GetCpuFanRpm(); s.CpuFanRpm = r > 300 && r < 5000 ? 2100000 / r : 0; } catch { }
            try { int r = NativeInterop.GetGpuFanRpm(); s.GpuFanRpm = r > 300 && r < 5000 ? 2100000 / r : 0; } catch { }

            try
            {
                bool cpuOdd = Math.Abs(s.CpuDuty - _targetDuty[0]) >= 5 ||
                    (s.CpuFanRpm > 4000 && _targetDuty[0] < 50);
                bool gpuOdd = Math.Abs(s.GpuDuty - _targetDuty[1]) >= 5 ||
                    (s.GpuFanRpm > 4000 && _targetDuty[1] < 50);
                if (cpuOdd || gpuOdd)
                    Log($"Fan check: CPU read={s.CpuDuty}% target={_targetDuty[0]}% rpm={s.CpuFanRpm} | GPU read={s.GpuDuty}% target={_targetDuty[1]}% rpm={s.GpuFanRpm}");
            }
            catch { }
        }

        if (_gpuInitialized)
        {
            try { s.GpuCoreClock = NativeInterop.Get_GPU_Graphics_Clock(); } catch { }
            try { s.GpuMemClock = NativeInterop.Get_GPU_Memory_Clock(); } catch { }
            try { s.GpuUsage = NativeInterop.Get_GPU_Util(); } catch { }
            try
            {
                IntPtr namePtr = NativeInterop.Get_GPU_name();
                if (namePtr != IntPtr.Zero) s.GpuName = Marshal.PtrToStringUni(namePtr) ?? "";
            }
            catch { }
        }

        return s;
    }

    public int CalculateDuty(int temp, List<TemperaturePoint> curve, bool linear)
    {
        if (curve.Count < 2) return 100; // 安全兜底：曲线无效时全速
        var sorted = curve.OrderBy(p => p.Temperature).ToList();
        if (temp <= sorted[0].Temperature) return (int)sorted[0].DutyPercent;
        if (temp >= sorted[^1].Temperature)
        {
            return (int)sorted[^1].DutyPercent;
        }
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            if (temp >= sorted[i].Temperature && temp < sorted[i + 1].Temperature)
            {
                if (linear)
                {
                    double ratio = (temp - sorted[i].Temperature) / (sorted[i + 1].Temperature - sorted[i].Temperature);
                    return (int)(sorted[i].DutyPercent + ratio * (sorted[i + 1].DutyPercent - sorted[i].DutyPercent));
                }
                return (int)sorted[i].DutyPercent;
            }
        }
        return 100; // 安全兜底：不应到达这里
    }

    public void UpdateTargetDuty(int cpuTemp, int? gpuTemp, int cpuCurrentDuty = -1, int gpuCurrentDuty = -1)
    {
        lock (_lock)
        {
            // === 最大风冷：直接100% ===
            if (_config.ForceCool)
            {
                _targetDuty[0] = 100;
                _targetDuty[1] = 100;
                _forceCoolActive = true;
                _currentDuty[0] = 100;
                _currentDuty[1] = 100;
                _cpuControlActive = true;
                _gpuControlActive = true;
                _lastCpuTemp = cpuTemp;
                _lastGpuTemp = gpuTemp ?? _lastGpuTemp;
                SetCpuFan(100);
                SetGpuFan(100);
                return;
            }
            _forceCoolActive = false;

            // === 重激活软控制：从实读转速播种 _currentDuty，避免从陈旧值突跳 ===
            if (!_cpuControlActive && cpuCurrentDuty >= 0)
                _currentDuty[0] = Math.Clamp(cpuCurrentDuty, 0, 100);
            if (gpuTemp.HasValue && !_gpuControlActive && gpuCurrentDuty >= 0)
                _currentDuty[1] = Math.Clamp(gpuCurrentDuty, 0, 100);

            // === 温度平滑（C++原版策略）===
            // 升温立即跟随，降温延迟 TransitionTemp 度，防止短暂温度波动
            int transition = Math.Max(0, _config.TransitionTemp);
            int gpuT = gpuTemp ?? _lastGpuTemp;

            _lastCpuTemp = Math.Max(_lastCpuTemp, cpuTemp);               // 升温：立即
            _lastCpuTemp = Math.Min(_lastCpuTemp, cpuTemp + transition);   // 降速：延迟
            _lastGpuTemp = Math.Max(_lastGpuTemp, gpuT);
            _lastGpuTemp = Math.Min(_lastGpuTemp, gpuT + transition);

            int effectiveCpu = _lastCpuTemp;
            int effectiveGpu = _lastGpuTemp;

            // === 计算目标占空比（温度平滑已防波动，直接计算）===
            _targetDuty[0] = CalculateDuty(effectiveCpu, _cpuCurve, _config.Linear);
            _gpuTempValid = gpuTemp.HasValue;
            if (gpuTemp.HasValue)
                _targetDuty[1] = CalculateDuty(effectiveGpu, _gpuCurve, _config.Linear);

            // 标记控制已激活：UpdateTargetDuty 仅在接管时调用，
            // 置位后 SoftControlLoop 才会驱动风扇（软控制模式必须）
            _cpuControlActive = true;
            if (gpuTemp.HasValue) _gpuControlActive = true;

            // === 立即应用（非软控制模式）===
            if (!_config.SoftControl)
            {
                _currentDuty[0] = _targetDuty[0];
                SetCpuFan(_currentDuty[0]);
                _cpuControlActive = true;
                if (gpuTemp.HasValue)
                {
                    _currentDuty[1] = _targetDuty[1];
                    SetGpuFan(_currentDuty[1]);
                    _gpuControlActive = true;
                }
                else if (_gpuControlActive)
                {
                    SetGpuFan(_currentDuty[1]);
                }
            }
        }
    }

    private void SoftControlLoop()
    {
        while (_running)
        {
            try
            {
                lock (_lock)
                {
                    if (_config.SoftControl && _config.TakeOver && !_forceCoolActive)
                    {
                        bool changed = false;
                        for (int i = 0; i < 2; i++)
                        {
                            // i==1(GPU)：温度无效时不调整目标，避免用陈旧 _targetDuty 误驱动
                            if (i == 1 && !_gpuTempValid) continue;

                            int gap = _targetDuty[i] - _currentDuty[i];
                            if (gap != 0)
                            {
                                // 自适应步长：差距越大，加速越快
                                // 差距≤5%: 1%/步（精细）  差距>20%: 最快5%/步
                                int step = Math.Abs(gap) <= 5 ? 1 : Math.Min(Math.Abs(gap) / 4, 5);
                                _currentDuty[i] += gap > 0 ? step : -step;
                                _currentDuty[i] = Math.Clamp(_currentDuty[i], 0, 100);
                                changed = true;
                            }
                        }

                        if (changed)
                        {
                            _softReassertCounter = 0;
                            if (_cpuControlActive) SetCpuFan(_currentDuty[0]);
                            if (_gpuControlActive && _gpuTempValid) SetGpuFan(_currentDuty[1]);
                        }
                        else if (++_softReassertCounter >= 10)
                        {
                            _softReassertCounter = 0;
                            if (_cpuControlActive) SetCpuFan(_currentDuty[0]);
                            if (_gpuControlActive && _gpuTempValid) SetGpuFan(_currentDuty[1]);
                        }
                    }
                }
                Thread.Sleep(100);
            }
            catch { }
        }
    }

    private void SetCpuFan(int dutyPct)
    {
        if (!_ecInitialized) return;
        try
        {
            int minDuty = Math.Clamp(_config.MinFanDuty, 1, 100);
            int safeDuty = Math.Clamp(dutyPct, minDuty, 100);
            if (safeDuty < dutyPct)
                Log($"SetFanDuty CPU requested={dutyPct}% clamped to {safeDuty}% (minDuty={minDuty})");
            NativeInterop.SetFanDuty(1, safeDuty * 255 / 100);
            if (safeDuty != _lastSentCpuDuty)
            {
                _lastSentCpuDuty = safeDuty;
                Log($"SetFanDuty CPU sent={safeDuty}%");
            }
        }
        catch { }
    }

    private void SetGpuFan(int dutyPct)
    {
        if (!_ecInitialized) return;
        try
        {
            int minDuty = Math.Clamp(_config.MinFanDuty, 1, 100);
            int safeDuty = Math.Clamp(dutyPct, minDuty, 100);
            if (safeDuty < dutyPct)
                Log($"SetFanDuty GPU requested={dutyPct}% clamped to {safeDuty}% (minDuty={minDuty})");
            NativeInterop.SetFanDuty(2, safeDuty * 255 / 100);
            try { NativeInterop.SetFanDuty(3, safeDuty * 255 / 100); } catch { }
            if (safeDuty != _lastSentGpuDuty)
            {
                _lastSentGpuDuty = safeDuty;
                Log($"SetFanDuty GPU sent={safeDuty}%");
            }
        }
        catch { }
    }

    public int CpuTargetDuty { get { lock (_lock) { return _targetDuty[0]; } } }
    public int GpuTargetDuty { get { lock (_lock) { return _targetDuty[1]; } } }

    public void ResetAllFans()
    {
        if (!_ecInitialized) return;
        lock (_lock)
        {
            // 释放控制权：标记未激活，下次接管时从实读转速重新播种 _currentDuty
            _cpuControlActive = false;
            _gpuControlActive = false;
        }
        try { NativeInterop.SetFanDutyAuto(1); } catch { }
        try { NativeInterop.SetFanDutyAuto(2); } catch { }
        try { NativeInterop.SetFanDutyAuto(3); } catch { }
    }

    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CyberFanControl.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private string GpuSnapshot()
    {
        try
        {
            int gc = NativeInterop.Get_GPU_Graphics_Clock();
            int mc = NativeInterop.Get_GPU_Memory_Clock();
            int util = NativeInterop.Get_GPU_Util();
            return $"Core={gc}MHz Mem={mc}MHz Util={util}%";
        }
        catch { return "GPU read failed"; }
    }

    // GPU Controls
    public bool LockGpuFrequency(int freq, int memOffset = 0)
    {
        if (!_gpuInitialized) return false;
        try
        {
            int gpuOC = freq > 0 ? freq : 0;
            int gpuClock = freq > 0 ? freq : 0;

            Log($"LockGpuFrequency: freq={freq}, memOffset={memOffset} | BEFORE {GpuSnapshot()}");

            int r3 = NativeInterop.Lock_Frequency(0, gpuClock);
            int r2 = NativeInterop.Set_MEMOC(0, memOffset);
            int r1 = NativeInterop.Set_CoreOC(0, gpuOC);

            Log($"  Lock_Frequency={r3:X2} Set_MEMOC={r2} Set_CoreOC={r1} | AFTER {GpuSnapshot()}");

            return (r1 == 0) && (r2 == 0) && (r3 == 0x19);
        }
        catch (Exception ex) { Log($"  Exception: {ex.Message}"); return false; }
    }

    public bool ApplyGpuSettings()
    {
        if (!_gpuInitialized) return false;

        int freq = 0;
        if (_config.LockGpuFreq && _config.GpuFreqLimit > 0)
        {
            freq = _config.GpuFreqLimit;
        }

        int memOffset = _config.LockMemOverclock ? _config.GpuMemOffset : 0;

        // 一次性应用：Lock_Frequency → Set_MEMOC → Set_CoreOC（Set_CoreOC 必须最后，触发偏移写入硬件）
        bool ok = LockGpuFrequency(freq, memOffset);
        Log($"ApplyGpuSettings result={(ok ? "OK" : "FAILED")} freq={freq} memOffset={memOffset}");
        return ok;
    }

    public void OnSystemResume()
    {
        lock (_lock)
        {
            _lastCpuTemp = int.MinValue;
            _lastGpuTemp = int.MinValue;
            // 恢复后控制需重新激活：让下次 UpdateTargetDuty 用实读转速重新播种 _currentDuty，
            // 避免软控制从挂起前的陈旧占空比起跳
            _cpuControlActive = false;
            _gpuControlActive = false;
            _forceCoolActive = false;
        }
        ApplyGpuSettings();
    }

    // Config
    public ConfigProfile Config => _config;
    public List<TemperaturePoint> CpuCurve => _cpuCurve;
    public List<TemperaturePoint> GpuCurve => _gpuCurve;

    public void SetCpuCurve(List<TemperaturePoint> curve)
    {
        lock (_lock)
        {
            _cpuCurve = curve;
            _lastCpuTemp = int.MinValue;
            _lastGpuTemp = int.MinValue;
        }
    }

    public void SetGpuCurve(List<TemperaturePoint> curve)
    {
        lock (_lock)
        {
            _gpuCurve = curve;
            _lastCpuTemp = int.MinValue;
            _lastGpuTemp = int.MinValue;
        }
    }

    public void SaveConfig(ConfigProfile config)
    {
        lock (_lock)
        {
            _config = config;
            _cpuCurve = config.CpuCurve ?? _cpuCurve;
            _gpuCurve = config.GpuCurve ?? _gpuCurve;
            _lastCpuTemp = int.MinValue;
            _lastGpuTemp = int.MinValue;
        }
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetConfigPath(), json);
        }
        catch (Exception ex) { Log($"SaveConfig failed: {ex.Message}"); }
    }

    public ConfigProfile? LoadConfig()
    {
        string path = GetConfigPath();
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            lock (_lock)
            {
                _config = JsonSerializer.Deserialize<ConfigProfile>(json) ?? new ConfigProfile();
                _cpuCurve = _config.CpuCurve ?? _cpuCurve;
                _gpuCurve = _config.GpuCurve ?? _gpuCurve;
                _lastCpuTemp = int.MinValue;
                _lastGpuTemp = int.MinValue;
            }
            return _config;
        }
        catch { return null; }
    }

    public void ResetToDefault()
    {
        lock (_lock)
        {
            _config = new ConfigProfile();
            _cpuCurve = DefaultCurve();
            _gpuCurve = DefaultCurve();
            _lastCpuTemp = int.MinValue;
            _lastGpuTemp = int.MinValue;
        }
        ResetAllFans();
        LockGpuFrequency(0, 0);
    }

    private string GetConfigPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CyberFanControl.json");

    public void Dispose()
    {
        _running = false;
        _softControlThread?.Join(1000);
        if (_ecInitialized) try { ResetAllFans(); } catch { }
        if (_gpuInitialized) try { LockGpuFrequency(0, 0); NativeInterop.CloseGPU_API(); } catch { }
        GC.SuppressFinalize(this);
    }
}
