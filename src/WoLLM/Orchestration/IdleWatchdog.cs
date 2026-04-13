using WoLLM.Config;

namespace WoLLM.Orchestration;

/// <summary>
/// Background service that monitors idle time and applies runtime-configurable idle actions
/// after the configured timeout. Checks every 60 seconds.
/// </summary>
public sealed class IdleWatchdog : BackgroundService
{
    private readonly ModelOrchestrator _orchestrator;
    private readonly ILogger<IdleWatchdog> _logger;

    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _idleTimeoutMinutes;
    private bool _shutdownOnIdle;
    private bool _unloadOnIdle;

    public IdleWatchdog(
        ModelOrchestrator orchestrator,
        WollmConfig config,
        ILogger<IdleWatchdog> logger)
    {
        _orchestrator = orchestrator;
        _logger       = logger;
        _idleTimeoutMinutes = config.IdleTimeoutMinutes;
        _shutdownOnIdle     = config.ShutdownOnIdle;
        _unloadOnIdle       = config.UnloadOnIdle;
    }

    /// <summary>
    /// Called by every API endpoint except GET /health.
    /// Thread-safe via Volatile.Write (single writer pattern).
    /// </summary>
    public void RecordActivity() =>
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public void UpdateSettings(int? idleTimeoutMinutes = null, bool? shutdownOnIdle = null, bool? unloadOnIdle = null)
    {
        if (idleTimeoutMinutes is int minutes)
            Interlocked.Exchange(ref _idleTimeoutMinutes, minutes);

        if (shutdownOnIdle is bool shutdown)
            _shutdownOnIdle = shutdown;

        if (unloadOnIdle is bool unload)
            _unloadOnIdle = unload;
    }

    public int IdleTimeoutMinutes => Volatile.Read(ref _idleTimeoutMinutes);
    public bool ShutdownOnIdle => _shutdownOnIdle;
    public bool UnloadOnIdle => _unloadOnIdle;

    public TimeSpan IdleFor =>
        DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IdleWatchdog started. Idle timeout: {Minutes} min. unloadOnIdle={UnloadOnIdle}, shutdownOnIdle={ShutdownOnIdle}.",
            IdleTimeoutMinutes, UnloadOnIdle, ShutdownOnIdle);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

            if (_orchestrator.CurrentModel is null)
                continue;

            var idle      = IdleFor;
            var threshold = TimeSpan.FromMinutes(IdleTimeoutMinutes);

            if (idle < threshold)
                continue;

            if (!UnloadOnIdle && !ShutdownOnIdle)
                continue;

            var modelName = _orchestrator.CurrentModel.Name;

            if (UnloadOnIdle)
            {
                _logger.LogInformation(
                    "Idle timeout reached ({IdleSeconds}s >= {ThresholdSeconds}s). Unloading model '{Model}'.",
                    (int)idle.TotalSeconds, (int)threshold.TotalSeconds,
                    modelName);

                await _orchestrator.UnloadForWatchdogAsync();

            }

            if (ShutdownOnIdle)
            {
                _logger.LogWarning("shutdown_on_idle=true — initiating system shutdown.");
                WoLLM.System.SystemShutdown.Shutdown(_logger);
            }
        }
    }
}
