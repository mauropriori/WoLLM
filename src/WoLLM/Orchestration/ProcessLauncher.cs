using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WoLLM.Orchestration;

public static class ProcessLauncher
{
    /// <summary>
    /// Starts the model script. On Windows runs the .bat via UseShellExecute=true
    /// (preserves GPU driver, CUDA, conda environment). On Linux invokes /bin/bash explicitly.
    /// </summary>
    public static Process Launch(string scriptPath, ILogger logger)
    {
        var psi = BuildStartInfo(scriptPath);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Exited += (_, _) =>
            logger.LogWarning("Script process exited (script: '{Script}').", scriptPath);

        process.Start();
        logger.LogInformation("Launched PID {Pid} via '{Script}'.", process.Id, scriptPath);
        return process;
    }

    private static ProcessStartInfo BuildStartInfo(string scriptPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo
            {
                FileName        = scriptPath,
                UseShellExecute = true,  // required: cmd.exe inherits user env (GPU, conda, PATH)
                CreateNoWindow  = false
            };
        }
        else
        {
            return new ProcessStartInfo
            {
                FileName        = "/bin/bash",
                Arguments       = $"-c \"{scriptPath.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            };
        }
    }
}
