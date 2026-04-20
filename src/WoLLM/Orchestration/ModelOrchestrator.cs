using System.Diagnostics;
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
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process? _currentProcess;
    private ModelConfig? _currentModel;
    private ModelConfig? _desiredModel;
    private LoadState _lastLoadState = LoadState.None;
    private SupervisorState _supervisorState = SupervisorState.Idle;
    private DateTimeOffset? _currentProcessStartedAtUtc;
    private DateTimeOffset? _lastUnexpectedExitAtUtc;
    private DateTimeOffset? _lastRestartAttemptAtUtc;
    private DateTimeOffset? _lastRestartSucceededAtUtc;
    private DateTimeOffset? _lastRestartFailureAtUtc;
    private string? _lastRestartFailure;
    private int? _lastExitCode;
    private int _restartCount;
    private int _consecutiveRestartFailures;
    private bool _isStoppingCurrentProcess;

    /// <summary>Currently healthy model. May be read without the lock (atomic reference read on 64-bit CLR).</summary>
    public ModelConfig? CurrentModel => _currentModel;
    public string? DesiredModelName => _desiredModel?.Name;
    public bool HasManagedModel => _desiredModel is not null;
    public LoadState LastLoadStatus => _lastLoadState;

    public ModelOrchestrator(
        WollmConfig config,
        ILogger<ModelOrchestrator> logger,
        IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
            ObserveExitedProcessLocked();

            var target = _config.Models.Find(
                m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown model: '{modelName}'.");

            if (string.Equals(_desiredModel?.Name, target.Name, StringComparison.OrdinalIgnoreCase) &&
                _currentProcess is not null &&
                !_currentProcess.HasExited &&
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

    /// <summary>Called by IdleWatchdog — acquires lock internally.</summary>
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
            ObserveExitedProcessLocked();

            if (_desiredModel is null)
                return;

            if (_currentProcess is not null &&
                !_currentProcess.HasExited &&
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
                    "Supervisor restart failed for model '{Model}'. Consecutive failures: {Failures}.",
                    model.Name,
                    _consecutiveRestartFailures);
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
            ObserveExitedProcessLocked();
            return CreateStatusSnapshotLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartModelLockedAsync(ModelConfig model, bool isRestart, CancellationToken ct)
    {
        _currentProcess = ProcessLauncher.Launch(model.ScriptPath, _logger);
        _currentProcessStartedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await WaitForHealthAsync(model, ct);
            _currentModel = model;
            _lastLoadState = LoadState.Loaded;
            _supervisorState = SupervisorState.Running;

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
        if (_currentProcess is null)
            return;

        _isStoppingCurrentProcess = true;
        try
        {
            if (!_currentProcess.HasExited)
            {
                _logger.LogInformation(
                    "Killing PID {Pid} (model '{Model}').",
                    _currentProcess.Id,
                    _currentModel?.Name ?? _desiredModel?.Name);

                _currentProcess.Kill(entireProcessTree: true);
                await _currentProcess.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while killing PID {Pid}.", _currentProcess.Id);
        }
        finally
        {
            _isStoppingCurrentProcess = false;
            DisposeCurrentProcessLocked();
        }
    }

    private void ObserveExitedProcessLocked()
    {
        if (_currentProcess is null || !_currentProcess.HasExited)
            return;

        var modelName = _currentModel?.Name ?? _desiredModel?.Name;
        int? exitCode = null;

        try
        {
            exitCode = _currentProcess.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read exit code for PID {Pid}.", _currentProcess.Id);
        }

        if (!_isStoppingCurrentProcess && _desiredModel is not null)
        {
            _lastUnexpectedExitAtUtc = DateTimeOffset.UtcNow;
            _lastExitCode = exitCode;
            _lastLoadState = LoadState.Failed;
            _supervisorState = SupervisorState.Failed;

            _logger.LogWarning(
                "Managed process for model '{Model}' exited unexpectedly. PID {Pid}, exit code {ExitCode}.",
                modelName,
                _currentProcess.Id,
                exitCode);
        }

        DisposeCurrentProcessLocked();
        _currentModel = null;
    }

    private void DisposeCurrentProcessLocked()
    {
        _currentProcess?.Dispose();
        _currentProcess = null;
        _currentProcessStartedAtUtc = null;
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
            ObserveExitedProcessLocked();

            if (_currentProcess is null)
            {
                throw new InvalidOperationException(
                    $"Model '{model.Name}' terminated before becoming healthy.");
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
            $"Model '{model.Name}' health check did not succeed within {_config.HealthCheckTimeoutSeconds}s.");
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
                ProcessId: _currentProcess is not null && !_currentProcess.HasExited ? _currentProcess.Id : null,
                ProcessStartedAtUtc: _currentProcessStartedAtUtc,
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
        if (_desiredModel is null || _currentProcess is not null || _lastRestartAttemptAtUtc is null)
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
        _currentProcess?.Dispose();
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
