using System;
using System.Runtime.InteropServices;

public static class NativeMethods
{
    [Flags]
    public enum EXECUTION_STATE : uint
    {
        ES_SYSTEM_REQUIRED = 0x00000001, // Sistemin uykuya geçmesini engeller
        ES_DISPLAY_REQUIRED = 0x00000002, // Ekranın kapanmasını / kararmasını engeller
        ES_CONTINUOUS = 0x80000000        // Ayarın kaldırılana kadar geçerli kalmasını sağlar
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);
}