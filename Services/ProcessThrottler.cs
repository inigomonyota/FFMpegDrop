using System.Diagnostics;
using FfmpegDrop.Helpers;

namespace FfmpegDrop.Services;

internal static class ProcessThrottler
{
    public static void SuspendProcess(Process process)
    {
        if (process == null) return;
        try { if (process.HasExited) return; } catch { return; }

        try
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr hThread = NativeMethods.OpenThread(0x0002, false, pT.Id);
                if (hThread == IntPtr.Zero) continue;
                try { NativeMethods.SuspendThread(hThread); }
                finally { NativeMethods.CloseHandle(hThread); }
            }
        }
        catch { /* ignore */ }
    }

    public static void ResumeProcess(Process process)
    {
        if (process == null) return;
        try { if (process.HasExited) return; } catch { return; }

        try
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr hThread = NativeMethods.OpenThread(0x0002, false, pT.Id);
                if (hThread == IntPtr.Zero) continue;

                try
                {
                    while (NativeMethods.ResumeThread(hThread) > 1) { }
                }
                finally { NativeMethods.CloseHandle(hThread); }
            }
        }
        catch { /* ignore */ }
    }
}
