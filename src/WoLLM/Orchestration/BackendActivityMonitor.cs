using System.Text;
using System.Text.Json;
using WoLLM.Config;

namespace WoLLM.Orchestration;

/// <summary>
/// Polls the active backend to infer real user activity and extends the idle timer conservatively.
/// </summary>
public sealed class BackendActivityMonitor : BackgroundService
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    private readonly WollmConfig _config;
    private readonly ModelOrchestrator _orchestrator;
    private readonly IdleWatchdog _watchdog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BackendActivityMonitor> _logger;
    private readonly object _sync = new();

    private BackendActivityStatusSnapshot _snapshot = new(
        Mode: "none",
        State: "idle",
        LastActivityAtUtc: null,
        LastCheckAtUtc: null,
        LastSuccessfulCheckAtUtc: null,
        LastError: null,
        ConsecutiveErrors: 0,
        ActivityEvidence: null);

    private string? _trackedModelName;
    private DateTimeOffset? _lastDetectedActivityAtUtc;
    private Dictionary<int, LlamaSlotObservation> _lastLlamaSlots = [];
    private int? _lastComfyHistoryCount;
    private bool _historyUnsupported;

    public BackendActivityMonitor(
        WollmConfig config,
        ModelOrchestrator orchestrator,
        IdleWatchdog watchdog,
        IHttpClientFactory httpClientFactory,
        ILogger<BackendActivityMonitor> logger)
    {
        _config = config;
        _orchestrator = orchestrator;
        _watchdog = watchdog;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public BackendActivityStatusSnapshot GetStatusSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackendActivityMonitor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var desiredModelName = _orchestrator.DesiredModelName;
            var model = desiredModelName is null
                ? null
                : _config.Models.Find(m => m.Name.Equals(desiredModelName, StringComparison.OrdinalIgnoreCase));

            if (model is null)
            {
                ResetTracking(null);
                await Task.Delay(IdlePollInterval, stoppingToken);
                continue;
            }

            if (!string.Equals(_trackedModelName, model.Name, StringComparison.OrdinalIgnoreCase))
                ResetTracking(model);

            await MonitorModelAsync(model, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(model.EffectiveActivityPollSeconds), stoppingToken);
        }
    }

    private async Task MonitorModelAsync(ModelConfig model, CancellationToken ct)
    {
        var mode = model.EffectiveActivityDetectionMode;
        if (mode == "none")
        {
            UpdateSnapshot(model.Name, mode, "idle", null, null, success: true);
            return;
        }

        try
        {
            var result = mode switch
            {
                "llama_slots" => await DetectLlamaActivityAsync(model, ct),
                "comfy_queue" => await DetectComfyActivityAsync(model, ct),
                _ => ActivityDetectionResult.Unsupported($"Unsupported detection mode '{mode}'.")
            };

            if (result.IsUnsupported)
            {
                UpdateSnapshot(model.Name, mode, "unsupported", null, result.Evidence, success: true);
                return;
            }

            if (result.IsActive)
            {
                _lastDetectedActivityAtUtc = DateTimeOffset.UtcNow;
                _watchdog.RecordActivity();
                UpdateSnapshot(model.Name, mode, "active", null, result.Evidence, success: true, _lastDetectedActivityAtUtc);
                return;
            }

            if (_lastDetectedActivityAtUtc is not null &&
                DateTimeOffset.UtcNow - _lastDetectedActivityAtUtc.Value <= TimeSpan.FromSeconds(model.EffectiveActivityGraceSeconds))
            {
                _watchdog.RecordActivity();
                UpdateSnapshot(
                    model.Name,
                    mode,
                    "active",
                    null,
                    $"{mode} grace window after activity at {_lastDetectedActivityAtUtc:O}",
                    success: true,
                    _lastDetectedActivityAtUtc);
                return;
            }

            UpdateSnapshot(model.Name, mode, "monitoring", null, null, success: true, _lastDetectedActivityAtUtc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backend activity detection failed for model '{Model}'.", model.Name);
            UpdateSnapshot(model.Name, mode, "degraded", ex.Message, null, success: false, _lastDetectedActivityAtUtc);
        }
    }

    private async Task<ActivityDetectionResult> DetectLlamaActivityAsync(ModelConfig model, CancellationToken ct)
    {
        using var response = await SendBackendRequestAsync(model, "/slots", ct);
        if (IsUnsupportedStatus(response.StatusCode))
            return ActivityDetectionResult.Unsupported("llama /slots endpoint is unavailable.");

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("llama /slots returned an unexpected payload.");

        var currentSlots = new Dictionary<int, LlamaSlotObservation>();
        string? evidence = null;
        var active = false;
        var index = 0;

        foreach (var slot in document.RootElement.EnumerateArray())
        {
            var slotId = TryGetInt32(slot, "id") ?? index;
            var isProcessing = TryGetBoolean(slot, "is_processing") ?? false;
            var taskId = TryGetInt64(slot, "id_task");
            var fingerprint = BuildLlamaFingerprint(slot);
            currentSlots[slotId] = new LlamaSlotObservation(taskId, fingerprint);

            if (isProcessing)
            {
                active = true;
                evidence ??= $"llama slot {slotId} processing";
            }
            else if (_lastLlamaSlots.TryGetValue(slotId, out var previous))
            {
                if (taskId is > 0 && previous.TaskId != taskId)
                {
                    active = true;
                    evidence ??= $"llama slot {slotId} task changed to {taskId}";
                }
                else if (!string.IsNullOrEmpty(fingerprint) &&
                         previous.TaskId == taskId &&
                         !string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    active = true;
                    evidence ??= $"llama slot {slotId} counters changed";
                }
            }
            else if (taskId is > 0)
            {
                active = true;
                evidence ??= $"llama slot {slotId} task {taskId} observed";
            }

            index++;
        }

        _lastLlamaSlots = currentSlots;
        return new ActivityDetectionResult(active, evidence, IsUnsupported: false);
    }

    private async Task<ActivityDetectionResult> DetectComfyActivityAsync(ModelConfig model, CancellationToken ct)
    {
        using var queueResponse = await SendBackendRequestAsync(model, "/queue", ct);
        if (IsUnsupportedStatus(queueResponse.StatusCode))
            return ActivityDetectionResult.Unsupported("ComfyUI /queue endpoint is unavailable.");

        queueResponse.EnsureSuccessStatusCode();

        var queueCount = await ReadComfyQueueCountAsync(queueResponse, ct);
        if (queueCount > 0)
            return new ActivityDetectionResult(true, $"comfy queue_remaining={queueCount}", IsUnsupported: false);

        if (_historyUnsupported)
            return new ActivityDetectionResult(false, null, IsUnsupported: false);

        using var historyResponse = await SendBackendRequestAsync(model, "/history", ct);
        if (IsUnsupportedStatus(historyResponse.StatusCode))
        {
            _historyUnsupported = true;
            return new ActivityDetectionResult(false, "ComfyUI /history endpoint is unavailable.", IsUnsupported: false);
        }

        historyResponse.EnsureSuccessStatusCode();
        var historyCount = await ReadComfyHistoryCountAsync(historyResponse, ct);

        if (_lastComfyHistoryCount is int previousHistoryCount && historyCount > previousHistoryCount)
        {
            _lastComfyHistoryCount = historyCount;
            return new ActivityDetectionResult(
                true,
                $"comfy history advanced from {previousHistoryCount} to {historyCount}",
                IsUnsupported: false);
        }

        _lastComfyHistoryCount = historyCount;
        return new ActivityDetectionResult(false, null, IsUnsupported: false);
    }

    private async Task<HttpResponseMessage> SendBackendRequestAsync(ModelConfig model, string path, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("backend-activity");
        return await client.GetAsync($"http://localhost:{model.Port}{path}", ct);
    }

    private async Task<int> ReadComfyQueueCountAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("ComfyUI /queue returned an unexpected payload.");

        var total = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;

            if (property.NameEquals("queue_running") ||
                property.NameEquals("queue_pending") ||
                property.NameEquals("running") ||
                property.NameEquals("pending"))
            {
                total += property.Value.GetArrayLength();
            }
        }

        return total;
    }

    private async Task<int> ReadComfyHistoryCountAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Object => document.RootElement.EnumerateObject().Count(),
            JsonValueKind.Array => document.RootElement.GetArrayLength(),
            _ => throw new InvalidOperationException("ComfyUI /history returned an unexpected payload.")
        };
    }

    private void ResetTracking(ModelConfig? model)
    {
        _trackedModelName = model?.Name;
        _lastDetectedActivityAtUtc = null;
        _lastLlamaSlots = [];
        _lastComfyHistoryCount = null;
        _historyUnsupported = false;

        lock (_sync)
        {
            _snapshot = new BackendActivityStatusSnapshot(
                Mode: model?.EffectiveActivityDetectionMode ?? "none",
                State: model is null ? "idle" : "monitoring",
                LastActivityAtUtc: null,
                LastCheckAtUtc: null,
                LastSuccessfulCheckAtUtc: null,
                LastError: null,
                ConsecutiveErrors: 0,
                ActivityEvidence: null);
        }
    }

    private void UpdateSnapshot(
        string modelName,
        string mode,
        string state,
        string? lastError,
        string? evidence,
        bool success,
        DateTimeOffset? lastActivityAtUtc = null)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var consecutiveErrors = success ? 0 : _snapshot.ConsecutiveErrors + 1;

            _snapshot = new BackendActivityStatusSnapshot(
                Mode: mode,
                State: state,
                LastActivityAtUtc: lastActivityAtUtc ?? _snapshot.LastActivityAtUtc,
                LastCheckAtUtc: now,
                LastSuccessfulCheckAtUtc: success ? now : _snapshot.LastSuccessfulCheckAtUtc,
                LastError: success ? null : lastError,
                ConsecutiveErrors: consecutiveErrors,
                ActivityEvidence: evidence);
        }
    }

    private static bool IsUnsupportedStatus(global::System.Net.HttpStatusCode statusCode) =>
        statusCode is global::System.Net.HttpStatusCode.NotFound or global::System.Net.HttpStatusCode.NotImplemented;

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string BuildLlamaFingerprint(JsonElement slot)
    {
        var builder = new StringBuilder();

        foreach (var property in slot.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number)
                continue;

            var name = property.Name.ToLowerInvariant();
            if (!name.Contains("token") && !name.Contains("processed") && !name.Contains("progress"))
                continue;

            builder.Append(property.Name);
            builder.Append('=');
            builder.Append(property.Value.GetRawText());
            builder.Append(';');
        }

        return builder.ToString();
    }

    private sealed record ActivityDetectionResult(bool IsActive, string? Evidence, bool IsUnsupported)
    {
        public static ActivityDetectionResult Unsupported(string evidence) =>
            new(false, evidence, IsUnsupported: true);
    }

    private sealed record LlamaSlotObservation(long? TaskId, string Fingerprint);
}

public sealed record BackendActivityStatusSnapshot(
    string Mode,
    string State,
    DateTimeOffset? LastActivityAtUtc,
    DateTimeOffset? LastCheckAtUtc,
    DateTimeOffset? LastSuccessfulCheckAtUtc,
    string? LastError,
    int ConsecutiveErrors,
    string? ActivityEvidence);
