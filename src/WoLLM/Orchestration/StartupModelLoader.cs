using WoLLM.Config;

namespace WoLLM.Orchestration;

/// <summary>
/// Optionally loads a configured model once the application host has started.
/// If startup loading fails, the error is logged and the host keeps running.
/// </summary>
public sealed class StartupModelLoader : BackgroundService
{
    private readonly WollmConfig _config;
    private readonly ModelOrchestrator _orchestrator;
    private readonly IdleWatchdog _watchdog;
    private readonly ILogger<StartupModelLoader> _logger;

    public StartupModelLoader(
        WollmConfig config,
        ModelOrchestrator orchestrator,
        IdleWatchdog watchdog,
        ILogger<StartupModelLoader> logger)
    {
        _config = config;
        _orchestrator = orchestrator;
        _watchdog = watchdog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var modelName = _config.LoadModelOnStartup;
        if (string.IsNullOrWhiteSpace(modelName))
            return;

        try
        {
            _logger.LogInformation(
                "Startup model load configured. Loading model '{Model}'.",
                modelName);

            await _orchestrator.SwitchAsync(modelName, stoppingToken);
            _watchdog.RecordActivity();

            _logger.LogInformation(
                "Startup model '{Model}' loaded successfully.",
                modelName);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load startup model '{Model}'. WoLLM will remain available for manual model loading.",
                modelName);
        }
    }
}
