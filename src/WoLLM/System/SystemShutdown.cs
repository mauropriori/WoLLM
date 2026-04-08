using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WoLLM.System;

public static class SystemShutdown
{
    /// <summary>
    /// Initiates an OS-level shutdown.
    /// Windows: shutdown /s /t 30  (30-second grace period)
    /// Linux:   shutdown -h +1     (1-minute minimum grace)
    /// </summary>
    public static void Shutdown(ILogger logger)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "shutdown",
                    Arguments       = "/s /t 30",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                logger.LogInformation("Windows shutdown initiated (30s grace).");
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "shutdown",
                    Arguments       = "-h +1",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                logger.LogInformation("Linux shutdown initiated (+1 min).");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initiate system shutdown.");
        }
    }
}
