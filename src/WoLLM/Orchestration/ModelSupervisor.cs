namespace WoLLM.Orchestration;

/// <summary>
/// Background service that keeps the desired model alive and restarts it after unexpected exits.
/// </summary>
public sealed class ModelSupervisor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ModelOrchestrator _orchestrator;
    private readonly ILogger<ModelSupervisor> _logger;

    public ModelSupervisor(
        ModelOrchestrator orchestrator,
        ILogger<ModelSupervisor> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ModelSupervisor started. Poll interval: {Seconds}s.",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _orchestrator.EnsureSupervisedModelAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected supervisor loop failure.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
