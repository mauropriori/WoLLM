using System.Diagnostics;
using System.Runtime.InteropServices;
using WoLLM.Config;
using WoLLM.Logging;

namespace WoLLM.Orchestration;

public interface IManagedProcessLauncher
{
    ManagedProcessLaunch Launch(ModelConfig model, ILogger logger);
}

public sealed class ProcessLauncher : IManagedProcessLauncher
{
    /// <summary>
    /// Starts the model script through the platform shell with stdout/stderr redirected
    /// into dedicated log files managed by WoLLM.
    /// </summary>
    public ManagedProcessLaunch Launch(ModelConfig model, ILogger logger)
    {
        var resolvedScriptPath = ResolveScriptPath(model.ScriptPath);
        var psi = BuildStartInfo(resolvedScriptPath);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();

        var launcherProcess = new SystemManagedProcessHandle(process);
        var logSession = ManagedProcessLogSession.Start(
            model.Name,
            launcherProcess.Id,
            launcherProcess.StandardOutputStream,
            launcherProcess.StandardErrorStream);
        var launch = new ManagedProcessLaunch(launcherProcess, logSession, model.Name, resolvedScriptPath);

        process.Exited += (_, _) =>
            logger.LogWarning(
                "Launcher process exited (model: '{Model}', script: '{Script}', stdout: '{StdoutLog}', stderr: '{StderrLog}').",
                model.Name,
                resolvedScriptPath,
                launch.LogPaths.StdoutPath,
                launch.LogPaths.StderrPath);

        logger.LogInformation(
            "Launched PID {Pid} for model '{Model}' via '{Script}'. stdout: '{StdoutLog}', stderr: '{StderrLog}'.",
            launcherProcess.Id,
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
    private IManagedProcessHandle? _backendProcess;

    public ManagedProcessLaunch(
        IManagedProcessHandle launcherProcess,
        ManagedProcessLogSession logSession,
        string modelName,
        string scriptPath)
    {
        LauncherProcess = launcherProcess;
        _logSession = logSession;
        ModelName = modelName;
        ScriptPath = scriptPath;
    }

    public IManagedProcessHandle LauncherProcess { get; }
    public string ModelName { get; }
    public string ScriptPath { get; }
    public ProcessLogPaths LogPaths => _logSession.Paths;
    public IManagedProcessHandle? BackendProcess => _backendProcess;
    public DateTimeOffset? BackendProcessStartedAtUtc { get; private set; }

    public Task WaitForLogDrainAsync() => _logSession.Completion;

    public void TrackBackendProcess(IManagedProcessHandle backendProcess, DateTimeOffset? startedAtUtc)
    {
        if (backendProcess.Id == LauncherProcess.Id)
        {
            if (!ReferenceEquals(backendProcess, LauncherProcess))
                backendProcess.Dispose();

            ResetTrackedBackendProcess();
            _backendProcess = LauncherProcess;
            BackendProcessStartedAtUtc = startedAtUtc ?? LauncherProcess.StartTimeUtc;
            return;
        }

        if (_backendProcess is not null &&
            !ReferenceEquals(_backendProcess, LauncherProcess) &&
            _backendProcess.Id != backendProcess.Id)
        {
            _backendProcess.Dispose();
        }

        if (_backendProcess is not null && _backendProcess.Id == backendProcess.Id)
        {
            if (!ReferenceEquals(_backendProcess, backendProcess))
                backendProcess.Dispose();
        }
        else
        {
            _backendProcess = backendProcess;
        }

        BackendProcessStartedAtUtc = startedAtUtc ?? _backendProcess?.StartTimeUtc;
    }

    public void ResetTrackedBackendProcess()
    {
        if (_backendProcess is not null && !ReferenceEquals(_backendProcess, LauncherProcess))
            _backendProcess.Dispose();

        _backendProcess = null;
        BackendProcessStartedAtUtc = null;
    }

    public void Dispose()
    {
        ResetTrackedBackendProcess();
        LauncherProcess.Dispose();
    }
}
