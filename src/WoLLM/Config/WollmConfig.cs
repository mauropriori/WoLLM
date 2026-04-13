namespace WoLLM.Config;

public sealed class WollmConfig
{
    public int Port { get; init; } = 8080;
    public int IdleTimeoutMinutes { get; init; } = 5;
    public bool ShutdownOnIdle { get; init; }
    public bool UnloadOnIdle { get; init; } = true;
    public int HealthCheckTimeoutSeconds { get; init; } = 120;
    public string? ApiKey { get; init; }
    public string? LoadModelOnStartup { get; init; }
    public List<ModelConfig> Models { get; init; } = [];
}

public sealed class ModelConfig
{
    public required string Name { get; init; }
    public required string Type { get; init; }  // "llama" | "comfyui"
    public required int Port { get; init; }
    public required string ScriptPath { get; init; }

    /// <summary>Health-check path determined by model type.</summary>
    public string HealthPath => Type.ToLowerInvariant() switch
    {
        "comfyui" => "/system_stats",
        _         => "/health"
    };
}
