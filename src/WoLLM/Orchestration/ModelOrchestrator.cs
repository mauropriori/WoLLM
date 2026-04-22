using WoLLM.Config;

namespace WoLLM.Orchestration;

public sealed class ModelOrchestrator : IDisposable
{
    public enum LoadState
    {
        None,
        Loading,
        Loaded,
        Failed
    }

    private enum SupervisorState
    {
        Idle,
        Starting,
        Running,
        Restarting,
        Failed
    }

    private readonly WollmConfig _config;
    private readonly ILogger<ModelOrchestrator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IManagedProcessLauncher _processLauncher;
    private readonly IBackendProcessResolver _backendProcessResolver;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ManagedProcessLaunch? _currentLaunch;
    private ModelConfig? _currentModel;
    private ModelConfig? _desiredModel;
    private LoadState _lastLoadState = LoadState.None;
    private SupervisorState _supervisorState = SupervisorState.Idle;
    private DateTimeOffset? _lastUnexpectedExitAtUtc;
    private DateTimeOffset? _lastRestartAttemptAtUtc;
    private DateTimeOffset? _lastRestartSucceededAtUtc;
    private DateTimeOffset? _lastRestartFailureAtUtc;
    private string? _lastRestartFailure;
    private int? _lastExitCode;
    private string? _lastStdoutLogPath;
    private string? _lastStderrLogPath;
    private int _restartCount;
    private int _consecutiveRestartFailures;
    private bool _isStoppingCurrentProcess;

    /// <summary>Currently supervised backend model. May be read without the lock (atomic reference read on 64-bit CLR).</summary>
    public ModelConfig? CurrentModel => _currentModel;
    public string? DesiredModelName => _desiredModel?.Name;
    public bool HasManagedModel => _desiredModel is not null;
    public LoadState LastLoadStatus => _lastLoadState;

    public ModelOrchestrator(
        WollmConfig config,
        ILogger<ModelOrchestrator> logger,
        IHttpClientFactory httpClientFactory,
        IManagedProcessLauncher processLauncher,
        IBackendProcessResolver backendProcessResolver)
    {
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _processLauncher = processLauncher;
        _backendProcessResolver = backendProcessResolver;
    }

    /// <summary>
    /// Loads the named model. No-op if already running and healthy.
    /// Kills the current model first if different.
    /// Blocks until the new model's health endpoint responds 200 or timeout elapses.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unknown model name.</exception>
    /// <exception cref="TimeoutException">Health check timed out; process has been killed.</exception>
    public async Task SwitchAsync(string modelName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await ObserveManagedProcessLockedAsync();

            var target = _config.Models.Find(
                m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown model: '{modelName}'.");

            if (string.Equals(_desiredModel?.Name, target.Name, StringComparison.OrdinalIgnoreCase) &&
                _currentLaunch?.BackendProcess is not null &&
                !_currentLaunch.BackendProcess.HasExited &&
                string.Equals(_currentModel?.Name, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoadState = LoadState.Loaded;
                _supervisorState = SupervisorState.Running;
                _logger.LogInformation("Model '{Model}' is already loaded.", modelName);
                return;
            }

            await StopManagedModelLockedAsync(clearDesiredModel: true);
            ResetSupervisorHistoryLocked();
            _lastLoadState = LoadState.Loading;
            _supervisorState = SupervisorState.Starting;

            try
            {
                await StartModelLockedAsync(target, isRestart: false, ct);
                _desiredModel = target;
                _logger.LogInformation("Model '{Model}' is ready.", modelName);
            }
            catch
            {
                _lastLoadState = LoadState.Failed;
                _supervisorState = SupervisorState.Failed;
                _desiredModel = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Kills the current model process and stops supervision.</summary>
    public async Task UnloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopManagedModelLockedAsync(clearDesiredModel: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Called by IdleWatchdog - acquires lock internally.</summary>
    internal async Task UnloadForWatchdogAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopManagedModelLockedAsync(clearDesiredModel: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ensures the desired model remains available. Called by the background supervisor.
    /// </summary>
    public async Task EnsureSupervisedModelAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await ObserveManagedProcessLockedAsync();

            if (_desiredModel is null)
                return;

            if (_currentLaunch?.BackendProcess is not null &&
                !_currentLaunch.BackendProcess.HasExited &&
                string.Equals(_currentModel?.Name, _desiredModel.Name, StringComparison.OrdinalIgnoreCase))
            {
                _supervisorState = SupervisorState.Running;
                return;
            }

            var nextRestartAtUtc = GetNextRestartAttemptAtUtcLocked();
            if (nextRestartAtUtc is not null && nextRestartAtUtc > DateTimeOffset.UtcNow)
                return;

            var model = _desiredModel;
            _restartCount++;
            _lastRestartAttemptAtUtc = DateTimeOffset.UtcNow;
            _lastLoadState = LoadState.Loading;
            _supervisorState = SupervisorState.Restarting;

            try
            {
                _logger.LogWarning(
                    "Supervisor restarting model '{Model}'. Attempt #{Attempt}.",
                    model.Name,
                    _restartCount);

                await StartModelLockedAsync(model, isRestart: true, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveRestartFailures++;
                _lastRestartFailureAtUtc = DateTimeOffset.UtcNow;
                _lastRestartFailure = ex.Message;
                _lastLoadState = LoadState.Failed;
                _supervisorState = SupervisorState.Failed;

                _logger.LogError(
                    ex,
                    "Supervisor restart failed for model '{Model}'. Consecutive failures: {Failures}. Latest stderr log: '{StderrLog}'.",
                    model.Name,
                    _consecutiveRestartFailures,
                    _lastStderrLogPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ModelRuntimeStatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await ObserveManagedProcessLockedAsync();
            return CreateStatusSnapshotLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartModelLockedAsync(ModelConfig model, bool isRestart, CancellationToken ct)
    {
        _currentLaunch = _processLauncher.Launch(model, _logger);
        _lastStdoutLogPath = _currentLaunch.LogPaths.StdoutPath;
        _lastStderrLogPath = _currentLaunch.LogPaths.StderrPath;

        try
        {
            await WaitForHealthAsync(model, ct);

            var resolution = ResolveTrackedBackendProcessLocked(model);
            if (!resolution.IsTrackedProcess)
            {
                throw new InvalidOperationException(
                    $"Model '{model.Name}' became healthy, but WoLLM could not verify the backend PID. {resolution.Reason}");
            }

            _currentLaunch.TrackBackendProcess(resolution.Process!, resolution.ProcessStartedAtUtc);
            _currentModel = model;
            _lastLoadState = LoadState.Loaded;
            _supervisorState = SupervisorState.Running;

            _logger.LogInformation(
                "Tracking backend PID {Pid} for model '{Model}'. {Reason}",
                _currentLaunch.BackendProcess?.Id,
                model.Name,
                resolution.Reason);

            if (isRestart)
            {
                _consecutiveRestartFailures = 0;
                _lastRestartSucceededAtUtc = DateTimeOffset.UtcNow;
                _lastRestartFailureAtUtc = null;
                _lastRestartFailure = null;
            }
        }
        catch
        {
            await StopCurrentProcessLockedAsync();
            throw;
        }
    }

    private async Task StopManagedModelLockedAsync(bool clearDesiredModel)
    {
        await StopCurrentProcessLockedAsync();
        _currentModel = null;

        if (clearDesiredModel)
        {
            _desiredModel = null;
            _lastLoadState = LoadState.None;
            _supervisorState = SupervisorState.Idle;
            ResetSupervisorHistoryLocked();
        }
    }

    private async Task StopCurrentProcessLockedAsync()
    {
        if (_currentLaunch is null)
            return;

        var launch = _currentLaunch;
        var launcherProcess = launch.LauncherProcess;
        var backendProcess = launch.BackendProcess;
        _isStoppingCurrentProcess = true;

        try
        {
            if (!launcherProcess.HasExited)
            {
                _logger.LogInformation(
                    "Killing launcher PID {Pid} (model '{Model}'). stdout: '{StdoutLog}', stderr: '{StderrLog}'.",
                    launcherProcess.Id,
                    _currentModel?.Name ?? _desiredModel?.Name,
                    _lastStdoutLogPath,
                    _lastStderrLogPath);

                launcherProcess.Kill(entireProcessTree: true);
                await launcherProcess.WaitForExitAsync();
            }

            if (backendProcess is not null &&
                backendProcess.Id != launcherProcess.Id &&
                !backendProcess.HasExited)
            {
                _logger.LogInformation(
                    "Killing backend PID {Pid} after launcher exit (model '{Model}').",
                    backendProcess.Id,
                    _currentModel?.Name ?? _desiredModel?.Name);

                backendProcess.Kill(entireProcessTree: true);
                await backendProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error while killing launcher PID {LauncherPid} / backend PID {BackendPid}.",
                launcherProcess.Id,
                backendProcess?.Id);
        }
        finally
        {
            _isStoppingCurrentProcess = false;
            await DisposeCurrentLaunchLockedAsync();
        }
    }

    private async Task ObserveManagedProcessLockedAsync()
    {
        if (_currentLaunch is null)
            return;

        if (_currentModel is null)
        {
            if (!_currentLaunch.LauncherProcess.HasExited)
                return;

            if (!_isStoppingCurrentProcess && _desiredModel is not null)
            {
                MarkUnexpectedExitLocked(
                    _currentLaunch.LauncherProcess.TryGetExitCode(),
                    $"Launcher PID {_currentLaunch.LauncherProcess.Id} exited before backend tracking was established.");
            }

            await DisposeCurrentLaunchLockedAsync();
            return;
        }

        var launch = _currentLaunch;
        var model = _currentModel;
        var resolution = ResolveTrackedBackendProcessLocked(model);

        if (resolution.IsTrackedProcess)
        {
            var previousPid = launch.BackendProcess?.Id;
            launch.TrackBackendProcess(resolution.Process!, resolution.ProcessStartedAtUtc);

            if (previousPid is null)
            {
                _logger.LogInformation(
                    "Tracking backend PID {Pid} for model '{Model}'. {Reason}",
                    launch.BackendProcess?.Id,
                    model.Name,
                    resolution.Reason);
            }
            else if (previousPid != launch.BackendProcess?.Id)
            {
                _logger.LogWarning(
                    "Backend PID changed for model '{Model}' from {PreviousPid} to {NewPid}. {Reason}",
                    model.Name,
                    previousPid,
                    launch.BackendProcess?.Id,
                    resolution.Reason);
            }

            _supervisorState = SupervisorState.Running;
            return;
        }

        if (!_isStoppingCurrentProcess && _desiredModel is not null)
        {
            var exitCode = launch.BackendProcess?.TryGetExitCode() ?? launch.LauncherProcess.TryGetExitCode();
            MarkUnexpectedExitLocked(exitCode, resolution.Reason);

            _logger.LogWarning(
                "Managed backend for model '{Model}' is unavailable. {Reason} Tracked backend PID: {BackendPid}; launcher PID: {LauncherPid}; stdout: '{StdoutLog}'; stderr: '{StderrLog}'.",
                model.Name,
                resolution.Reason,
                launch.BackendProcess?.Id,
                launch.LauncherProcess.Id,
                launch.LogPaths.StdoutPath,
                launch.LogPaths.StderrPath);
        }

        await StopCurrentProcessLockedAsync();
    }

    private void MarkUnexpectedExitLocked(int? exitCode, string reason)
    {
        _lastUnexpectedExitAtUtc = DateTimeOffset.UtcNow;
        _lastExitCode = exitCode;
        _lastLoadState = LoadState.Failed;
        _supervisorState = SupervisorState.Failed;
        _currentModel = null;

        _logger.LogWarning(
            "Managed process for model '{Model}' became unavailable. Exit code {ExitCode}. {Reason}",
            _desiredModel?.Name,
            exitCode,
            reason);
    }

    private BackendProcessResolution ResolveTrackedBackendProcessLocked(ModelConfig model)
    {
        return _currentLaunch is null
            ? BackendProcessResolution.Missing($"Model '{model.Name}' has no active launcher process.")
            : _backendProcessResolver.Resolve(model, _currentLaunch);
    }

    private async Task DisposeCurrentLaunchLockedAsync()
    {
        var currentLaunch = _currentLaunch;
        _currentLaunch = null;
        _currentModel = null;

        if (currentLaunch is null)
            return;

        try
        {
            await currentLaunch.WaitForLogDrainAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error while finalizing process log capture for launcher PID {Pid}.",
                currentLaunch.LauncherProcess.Id);
        }
        finally
        {
            currentLaunch.Dispose();
        }
    }

    private async Task WaitForHealthAsync(ModelConfig model, CancellationToken ct)
    {
        var url = $"http://localhost:{model.Port}{model.HealthPath}";
        var deadline = DateTime.UtcNow.AddSeconds(_config.HealthCheckTimeoutSeconds);
        var http = _httpClientFactory.CreateClient("healthcheck");

        _logger.LogInformation(
            "Polling health at {Url} (timeout {Seconds}s).",
            url,
            _config.HealthCheckTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await ObserveManagedProcessLockedAsync();

            if (_currentLaunch is null)
            {
                throw new InvalidOperationException(
                    $"Model '{model.Name}' terminated before becoming healthy. See stderr log at '{_lastStderrLogPath}'.");
            }

            try
            {
                var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Process not ready yet; keep polling.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException(
            $"Model '{model.Name}' health check did not succeed within {_config.HealthCheckTimeoutSeconds}s. Latest stderr log: '{_lastStderrLogPath}'.");
    }

    private ModelRuntimeStatusSnapshot CreateStatusSnapshotLocked()
    {
        return new ModelRuntimeStatusSnapshot(
            CurrentModel: _currentModel?.Name,
            DesiredModel: _desiredModel?.Name,
            LoadStatus: _lastLoadState.ToString().ToLowerInvariant(),
            Supervisor: new SupervisorStatusSnapshot(
                State: _supervisorState.ToString().ToLowerInvariant(),
                DesiredModel: _desiredModel?.Name,
                ProcessId: _currentLaunch?.BackendProcess is not null && !_currentLaunch.BackendProcess.HasExited
                    ? _currentLaunch.BackendProcess.Id
                    : null,
                ProcessStartedAtUtc: _currentLaunch?.BackendProcessStartedAtUtc,
                RestartCount: _restartCount,
                ConsecutiveRestartFailures: _consecutiveRestartFailures,
                LastUnexpectedExitAtUtc: _lastUnexpectedExitAtUtc,
                LastExitCode: _lastExitCode,
                LastRestartAttemptAtUtc: _lastRestartAttemptAtUtc,
                LastRestartSucceededAtUtc: _lastRestartSucceededAtUtc,
                LastRestartFailureAtUtc: _lastRestartFailureAtUtc,
                LastRestartFailure: _lastRestartFailure,
                NextRestartAttemptAtUtc: GetNextRestartAttemptAtUtcLocked()));
    }

    private DateTimeOffset? GetNextRestartAttemptAtUtcLocked()
    {
        if (_desiredModel is null || _currentLaunch is not null || _lastRestartAttemptAtUtc is null)
            return null;

        if (_consecutiveRestartFailures <= 0)
            return _lastRestartAttemptAtUtc;

        return _lastRestartAttemptAtUtc.Value + GetRestartBackoff(_consecutiveRestartFailures);
    }

    private static TimeSpan GetRestartBackoff(int consecutiveRestartFailures) => consecutiveRestartFailures switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    private void ResetSupervisorHistoryLocked()
    {
        _restartCount = 0;
        _consecutiveRestartFailures = 0;
        _lastUnexpectedExitAtUtc = null;
        _lastExitCode = null;
        _lastRestartAttemptAtUtc = null;
        _lastRestartSucceededAtUtc = null;
        _lastRestartFailureAtUtc = null;
        _lastRestartFailure = null;
    }

    public void Dispose()
    {
        _lock.Dispose();
        _currentLaunch?.Dispose();
    }
}

public sealed record ModelRuntimeStatusSnapshot(
    string? CurrentModel,
    string? DesiredModel,
    string LoadStatus,
    SupervisorStatusSnapshot Supervisor);

public sealed record SupervisorStatusSnapshot(
    string State,
    string? DesiredModel,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    int RestartCount,
    int ConsecutiveRestartFailures,
    DateTimeOffset? LastUnexpectedExitAtUtc,
    int? LastExitCode,
    DateTimeOffset? LastRestartAttemptAtUtc,
    DateTimeOffset? LastRestartSucceededAtUtc,
    DateTimeOffset? LastRestartFailureAtUtc,
    string? LastRestartFailure,
    DateTimeOffset? NextRestartAttemptAtUtc);
