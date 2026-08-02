using System.Runtime.InteropServices;

namespace CyberFanControl.Services;

public static class NativeInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ECData
    {
        public byte Remote;
        public byte Local;
        public byte FanDuty;
        public byte Reserve;
    }

    // ==================== ClevoEcInfo.dll ====================
    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitIo();

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetFanDuty(int fanId, int duty);

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetFanDutyAuto(int fanId);

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern ECData GetTempFanDuty(int fanId);

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetFanCount();

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetCpuFanRpm();

    [DllImport("ClevoEcInfo.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetGpuFanRpm();

    // ==================== NVGPU_DLL.dll ====================
    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int InitGPU_API();

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Set_GPU_Number(int gpuNumber);

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Get_GPU_Graphics_Clock();

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Get_GPU_Memory_Clock();

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Get_GPU_Util();

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Get_GPU_name();

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Lock_Frequency(int gpuNumber, int frequency);

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Set_CoreOC(int gpuNumber, int offset);

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Set_MEMOC(int gpuNumber, int offset);

    [DllImport("NVGPU_DLL.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseGPU_API();
}
