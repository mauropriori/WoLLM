using WoLLM.Config;

namespace WoLLM.Orchestration;

/// <summary>
/// Background service that monitors idle time and unloads models (and optionally shuts down the PC)
/// after the configured timeout. Checks every 60 seconds.
/// </summary>
public sealed class IdleWatchdog : BackgroundService
{
    private readonly ModelOrchestrator _orchestrator;
    private readonly WollmConfig _config;
    private readonly ILogger<IdleWatchdog> _logger;

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private bool _shutdownOnIdle;

    public IdleWatchdog(
        ModelOrchestrator orchestrator,
        WollmConfig config,
        ILogger<IdleWatchdog> logger)
    {
        _orchestrator = orchestrator;
        _config       = config;
        _logger       = logger;
    }

    /// <summary>
    /// Called by every API endpoint except GET /health.
    /// Thread-safe via Volatile.Write (single writer pattern).
    /// </summary>
    public void RecordActivity() =>
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public void SetShutdownOnIdle(bool value) =>
        _shutdownOnIdle = value;

    public bool ShutdownOnIdle => _shutdownOnIdle;

    public TimeSpan IdleFor =>
        DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IdleWatchdog started. Idle timeout: {Minutes} min.",
            _config.IdleTimeoutMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            if (_orchestrator.CurrentModel is null)
                continue;

            var idle      = IdleFor;
            var threshold = TimeSpan.FromMinutes(_config.IdleTimeoutMinutes);

            if (idle < threshold)
                continue;

            _logger.LogInformation(
                "Idle timeout reached ({IdleSeconds}s >= {ThresholdSeconds}s). Unloading model '{Model}'.",
                (int)idle.TotalSeconds, (int)threshold.TotalSeconds,
                _orchestrator.CurrentModel.Name);

            await _orchestrator.UnloadForWatchdogAsync();

            if (_shutdownOnIdle)
            {
                _logger.LogWarning("shutdown_on_idle=true — initiating system shutdown.");
                WoLLM.System.SystemShutdown.Shutdown(_logger);
            }
        }
    }
}
