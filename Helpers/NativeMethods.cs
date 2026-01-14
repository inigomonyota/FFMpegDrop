using System.Runtime.InteropServices;

namespace FfmpegDrop.Helpers;

internal static class NativeMethods
{
    // ========== kernel32.dll - Thread Suspension ==========
    [DllImport("kernel32.dll")]
    internal static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, int dwThreadId);

    [DllImport("kernel32.dll")]
    internal static extern int SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    internal static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    internal static extern int CloseHandle(IntPtr hObject);

    // ========== dwmapi.dll - Dark Mode ==========
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
}
