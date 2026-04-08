using System.Diagnostics;
using System.Runtime.InteropServices;
using WoLLM.Config;

namespace WoLLM.Orchestration;

public sealed class ModelOrchestrator : IDisposable
{
    private readonly WollmConfig _config;
    private readonly ILogger<ModelOrchestrator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process? _currentProcess;
    private ModelConfig? _currentModel;

    /// <summary>Currently loaded model. May be read without the lock (atomic reference read on 64-bit CLR).</summary>
    public ModelConfig? CurrentModel => _currentModel;

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
    /// Loads the named model. No-op if already loaded. Kills the current model first if different.
    /// Blocks until the new model's health endpoint responds 200 or timeout elapses.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unknown model name.</exception>
    /// <exception cref="TimeoutException">Health check timed out; process has been killed.</exception>
    public async Task SwitchAsync(string modelName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentModel?.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Model '{Model}' is already loaded.", modelName);
                return;
            }

            var target = _config.Models.Find(
                m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown model: '{modelName}'.");

            await KillCurrentAsync();

            var scriptPath = RuntimeScript(target);
            _currentProcess = ProcessLauncher.Launch(scriptPath, _logger);
            _currentModel   = target;

            await WaitForHealthAsync(target, ct);
            _logger.LogInformation("Model '{Model}' is ready.", modelName);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Kills the current model process. Does nothing if no model is loaded.</summary>
    public async Task UnloadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await KillCurrentAsync();
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
            await KillCurrentAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task KillCurrentAsync()
    {
        if (_currentProcess is null) return;

        try
        {
            if (!_currentProcess.HasExited)
            {
                _logger.LogInformation(
                    "Killing PID {Pid} (model '{Model}').",
                    _currentProcess.Id, _currentModel?.Name);
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
            _currentProcess.Dispose();
            _currentProcess = null;
            _currentModel   = null;
        }
    }

    private async Task WaitForHealthAsync(ModelConfig model, CancellationToken ct)
    {
        var url      = $"http://localhost:{model.Port}{model.HealthPath}";
        var deadline = DateTime.UtcNow.AddSeconds(_config.HealthCheckTimeoutSeconds);
        var http     = _httpClientFactory.CreateClient("healthcheck");

        _logger.LogInformation(
            "Polling health at {Url} (timeout {Seconds}s).",
            url, _config.HealthCheckTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Process not ready yet — keep polling
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        // Timeout — clean up what we started
        await KillCurrentAsync();
        throw new TimeoutException(
            $"Model '{model.Name}' health check did not succeed within {_config.HealthCheckTimeoutSeconds}s.");
    }

    private static string RuntimeScript(ModelConfig model) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? model.Script.Win
            : model.Script.Unix;

    public void Dispose()
    {
        _lock.Dispose();
        _currentProcess?.Dispose();
    }
}
