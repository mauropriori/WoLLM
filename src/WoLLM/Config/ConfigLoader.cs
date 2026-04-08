using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoLLM.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads and validates wollm.json from the executable directory.
    /// Writes an example config and exits with code 1 if the file is missing.
    /// Prints all validation errors and exits with code 1 if config is invalid.
    /// </summary>
    public static WollmConfig Load(ILogger logger)
    {
        var exeDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(exeDir, "wollm.json");

        if (!File.Exists(configPath))
        {
            var examplePath = Path.Combine(exeDir, "wollm.example.json");
            WriteExampleConfig(examplePath);
            logger.LogError(
                "Config file not found: {ConfigPath}\n" +
                "An example config has been written to: {ExamplePath}\n" +
                "Rename it to wollm.json, edit the model paths, then restart WoLLM.",
                configPath, examplePath);
            Environment.Exit(1);
        }

        WollmConfig config;
        try
        {
            var json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize<WollmConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Config deserialized to null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse {ConfigPath}", configPath);
            Environment.Exit(1);
            return null!; // unreachable
        }

        var errors = Validate(config);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                logger.LogError("Config validation error: {Error}", error);
            Environment.Exit(1);
        }

        logger.LogInformation(
            "Config loaded: {ModelCount} model(s), port {Port}, idle timeout {IdleTimeoutMinutes} min.",
            config.Models.Count, config.Port, config.IdleTimeoutMinutes);

        return config;
    }

    private static List<string> Validate(WollmConfig config)
    {
        var errors = new List<string>();

        if (config.Port is < 1 or > 65535)
            errors.Add($"port {config.Port} is out of range 1–65535.");

        if (config.IdleTimeoutMinutes < 1)
            errors.Add("idleTimeoutMinutes must be >= 1.");

        if (config.HealthCheckTimeoutSeconds < 1)
            errors.Add("healthCheckTimeoutSeconds must be >= 1.");

        if (config.Models.Count == 0)
            errors.Add("models array is empty — define at least one model.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in config.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                errors.Add("A model entry has an empty name.");
                continue;
            }

            if (!names.Add(model.Name))
                errors.Add($"Duplicate model name: '{model.Name}'.");

            if (model.Port is < 1 or > 65535)
                errors.Add($"Model '{model.Name}': port {model.Port} is out of range.");

            var scriptPath = RuntimeScript(model);
            var fullPath = Path.IsPathRooted(scriptPath)
                ? scriptPath
                : Path.Combine(AppContext.BaseDirectory, scriptPath);

            if (!File.Exists(fullPath))
                errors.Add($"Model '{model.Name}': script not found at '{fullPath}'.");
        }

        return errors;
    }

    private static string RuntimeScript(ModelConfig model) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? model.Script.Win
            : model.Script.Unix;

    private static void WriteExampleConfig(string path)
    {
        var example = new
        {
            port = 8080,
            apiKey = "",
            idleTimeoutMinutes = 5,
            healthCheckTimeoutSeconds = 120,
            models = new[]
            {
                new
                {
                    name   = "mistral-7b",
                    type   = "llama",
                    port   = 8081,
                    script = new { win = "scripts/mistral-7b.bat", unix = "scripts/mistral-7b.sh" }
                },
                new
                {
                    name   = "sdxl",
                    type   = "comfyui",
                    port   = 8188,
                    script = new { win = "scripts/comfyui-sdxl.bat", unix = "scripts/comfyui-sdxl.sh" }
                }
            }
        };

        var json = JsonSerializer.Serialize(example, JsonOptions);
        File.WriteAllText(path, json);
    }
}
