namespace CyberFanControl.Models;

public class TemperaturePoint
{
    public double Temperature { get; set; }
    public double DutyPercent { get; set; }
    public TemperaturePoint(double temperature, double dutyPercent)
    {
        Temperature = temperature;
        DutyPercent = dutyPercent;
    }
}

public class FanStatus
{
    public int CpuTemp { get; set; }
    public int GpuTemp { get; set; }
    public bool GpuTempValid { get; set; }
    public int CpuFanRpm { get; set; }
    public int GpuFanRpm { get; set; }
    public int CpuDuty { get; set; }
    public int GpuDuty { get; set; }
    // GPU
    public string GpuName { get; set; } = "";
    public int GpuCoreClock { get; set; }
    public int GpuMemClock { get; set; }
    public int GpuUsage { get; set; }
}

public class ConfigProfile
{
    public int Interval { get; set; } = 2;
    public int TransitionTemp { get; set; } = 3;
    public int MinFanDuty { get; set; } = 18;
    public bool TakeOver { get; set; }
    public bool Linear { get; set; }
    public bool SoftControl { get; set; }
    public bool ForceCool { get; set; }
    // GPU
    public bool LockGpuFreq { get; set; }
    public int GpuFreqLimit { get; set; }
    public bool LockMemOverclock { get; set; }
    public int GpuMemOffset { get; set; }
    // Curves
    public List<TemperaturePoint>? CpuCurve { get; set; }
    public List<TemperaturePoint>? GpuCurve { get; set; }
}
