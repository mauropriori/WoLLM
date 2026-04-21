using System.Diagnostics;
using System.Runtime.InteropServices;
using WoLLM.Config;
using WoLLM.Logging;

namespace WoLLM.Orchestration;

public static class ProcessLauncher
{
    /// <summary>
    /// Starts the model script through the platform shell with stdout/stderr redirected
    /// into dedicated log files managed by WoLLM.
    /// </summary>
    public static ManagedProcessLaunch Launch(ModelConfig model, ILogger logger)
    {
        var resolvedScriptPath = ResolveScriptPath(model.ScriptPath);
        var psi = BuildStartInfo(resolvedScriptPath);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();

        var logSession = ManagedProcessLogSession.Start(model.Name, process);
        var launch = new ManagedProcessLaunch(process, logSession, model.Name, resolvedScriptPath);

        process.Exited += (_, _) =>
            logger.LogWarning(
                "Script process exited (model: '{Model}', script: '{Script}', stdout: '{StdoutLog}', stderr: '{StderrLog}').",
                model.Name,
                resolvedScriptPath,
                launch.LogPaths.StdoutPath,
                launch.LogPaths.StderrPath);

        logger.LogInformation(
            "Launched PID {Pid} for model '{Model}' via '{Script}'. stdout: '{StdoutLog}', stderr: '{StderrLog}'.",
            process.Id,
            model.Name,
            resolvedScriptPath,
            launch.LogPaths.StdoutPath,
            launch.LogPaths.StderrPath);

        return launch;
    }

    private static ProcessStartInfo BuildStartInfo(string scriptPath)
    {
        var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/d /s /c \"\"{scriptPath}\"\"",
                WorkingDirectory       = workingDirectory,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
        }

        return new ProcessStartInfo
        {
            FileName               = "/bin/bash",
            Arguments              = $"\"{scriptPath.Replace("\"", "\\\"")}\"",
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
    }

    private static string ResolveScriptPath(string scriptPath)
    {
        var resolved = Path.IsPathRooted(scriptPath)
            ? scriptPath
            : Path.Combine(AppContext.BaseDirectory, scriptPath);

        return Path.GetFullPath(resolved);
    }
}

public sealed class ManagedProcessLaunch : IDisposable
{
    private readonly ManagedProcessLogSession _logSession;

    public ManagedProcessLaunch(
        Process process,
        ManagedProcessLogSession logSession,
        string modelName,
        string scriptPath)
    {
        Process = process;
        _logSession = logSession;
        ModelName = modelName;
        ScriptPath = scriptPath;
    }

    public Process Process { get; }
    public string ModelName { get; }
    public string ScriptPath { get; }
    public ProcessLogPaths LogPaths => _logSession.Paths;

    public Task WaitForLogDrainAsync() => _logSession.Completion;

    public void Dispose() => Process.Dispose();
}
