using Serilog;
using System.Reflection;
using WoLLM.Config;
using WoLLM.Logging;
using WoLLM.Orchestration;
using WoLLM.System;

// Bootstrap logger so config errors are printed cleanly before DI is ready.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StartupLogStore>();

builder.Host.UseSerilog((_, services, logConfig) =>
    logConfig
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.Sink(new StartupLogSink(services.GetRequiredService<StartupLogStore>()))
        .WriteTo.File("logs/wollm-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7));

// Load and validate config — exits with code 1 on any error.
// Use a temporary MEL logger backed by Serilog for early startup.
using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSerilog());
var config = ConfigLoader.Load(bootstrapLoggerFactory.CreateLogger(nameof(ConfigLoader)));

// Register services.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IManagedProcessLauncher, ProcessLauncher>();
builder.Services.AddSingleton<IBackendProcessResolver, BackendProcessResolver>();
builder.Services.AddSingleton<ModelOrchestrator>();
builder.Services.AddSingleton<ModelSupervisor>();
builder.Services.AddSingleton<BackendActivityMonitor>();
builder.Services.AddSingleton<IdleWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelSupervisor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackendActivityMonitor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
builder.Services.AddHostedService<StartupModelLoader>();

// Short timeout per health-check probe; failures are retried every second by the orchestrator.
builder.Services.AddHttpClient("healthcheck", client =>
    client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("backend-activity", client =>
    client.Timeout = TimeSpan.FromSeconds(3));

// Bind to configured port on all interfaces.
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(config.Port));

var app = builder.Build();
app.UseSerilogRequestLogging();
var informationalVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";
app.Logger.LogInformation("Starting WoLLM v{Version}.", informationalVersion);

// API key guard — only active when apiKey is configured (non-empty = protected mode).
if (!string.IsNullOrWhiteSpace(config.ApiKey))
{
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != config.ApiKey)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized: invalid or missing API key." });
            return;
        }
        await next(ctx);
    });
}

// Resolve singletons eagerly to surface DI issues at startup.
var orchestrator = app.Services.GetRequiredService<ModelOrchestrator>();
var activityMonitor = app.Services.GetRequiredService<BackendActivityMonitor>();
var watchdog     = app.Services.GetRequiredService<IdleWatchdog>();

// ── Endpoints ────────────────────────────────────────────────────────────────

// GET /health — intentionally does NOT update idle timer (keepalives must not prevent shutdown).
app.MapGet("/health", async (CancellationToken ct) =>
{
    var runtime = await orchestrator.GetStatusAsync(ct);
    return Results.Ok(new
    {
        status       = "ok",
        currentModel = runtime.CurrentModel,
        desiredModel = runtime.DesiredModel,
        loadStatus   = runtime.LoadStatus
    });
});

// POST /set?idleTimeoutMinutes=5&shutdown_on_idle=true|false&unload_on_idle=true|false
app.MapPost("/set", (int? idleTimeoutMinutes, bool? shutdown_on_idle, bool? unload_on_idle) =>
{
    if (idleTimeoutMinutes is < 1)
    {
        return Results.BadRequest(new
        {
            error = "idleTimeoutMinutes must be >= 1."
        });
    }

    watchdog.UpdateSettings(idleTimeoutMinutes, shutdown_on_idle, unload_on_idle);
    watchdog.RecordActivity();
    return Results.Ok(new
    {
        status             = "ok",
        idleTimeoutMinutes = watchdog.IdleTimeoutMinutes,
        shutdownOnIdle     = watchdog.ShutdownOnIdle,
        unloadOnIdle       = watchdog.UnloadOnIdle
    });
});

// POST /load/{modelName}
app.MapPost("/load/{modelName}", async (string modelName, CancellationToken ct) =>
{
    watchdog.RecordActivity();
    try
    {
        await orchestrator.SwitchAsync(modelName, ct);
        return Results.Ok(new { status = "loaded", model = modelName });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(
            title:      "Model startup timed out",
            detail:     ex.Message,
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

// POST /unload
app.MapPost("/unload", async (CancellationToken ct) =>
{
    watchdog.RecordActivity();
    await orchestrator.UnloadAsync();
    return Results.Ok(new { status = "unloaded" });
});

// POST /shutdown?forceShutdown=true|false
app.MapPost("/shutdown", async (bool? forceShutdown) =>
{
    bool? wolBoot       = await WolDetector.WasWolBootAsync();
    bool  allowedByFlag = wolBoot == true || watchdog.ShutdownOnIdle;

    if (!allowedByFlag && forceShutdown != true)
    {
        app.Logger.LogWarning(
            "Shutdown rejected: wolBoot={WolBoot}, shutdownOnIdle={ShutdownOnIdle}, forceShutdown={ForceShutdown}.",
            wolBoot, watchdog.ShutdownOnIdle, forceShutdown);
        return Results.BadRequest(new
        {
            error = "Shutdown requires forceShutdown=true (system was not booted via WoL and shutdown_on_idle is not set)."
        });
    }

    app.Logger.LogWarning(
        "Shutdown accepted. wolBoot={WolBoot}, shutdownOnIdle={ShutdownOnIdle}, forceShutdown={ForceShutdown}.",
        wolBoot, watchdog.ShutdownOnIdle, forceShutdown);

    SystemShutdown.Shutdown(app.Logger);
    return Results.Ok(new { message = "Shutdown initiated." });
});

// GET /status — does NOT update idle timer
app.MapGet("/status", async () =>
{
    var runtime = await orchestrator.GetStatusAsync();
    var activity = activityMonitor.GetStatusSnapshot();
    var sysTask = SystemStats.GetAsync();
    var wolTask = WolDetector.WasWolBootAsync();
    await Task.WhenAll(sysTask, wolTask);

    var sys = sysTask.Result;
    return Results.Ok(new
    {
        currentModel       = runtime.CurrentModel,
        desiredModel       = runtime.DesiredModel,
        loadStatus         = runtime.LoadStatus,
        shutdownOnIdle     = watchdog.ShutdownOnIdle,
        unloadOnIdle       = watchdog.UnloadOnIdle,
        idleTimeoutMinutes = watchdog.IdleTimeoutMinutes,
        idleSeconds        = (int)watchdog.IdleFor.TotalSeconds,
        wolBoot            = wolTask.Result,
        supervisor         = runtime.Supervisor,
        activityMonitor    = activity,
        system = new
        {
            cpus       = sys.Cpus,
            ramUsedMb  = sys.RamUsedMb,
            ramTotalMb = sys.RamTotalMb,
            gpus       = sys.Gpus
        }
    });
});

// GET /models — does NOT update idle timer
app.MapGet("/models", () =>
    Results.Ok(new
    {
        models = config.Models.Select(m => new
        {
            name = m.Name,
            type = m.Type
        })
    }));

// GET /logs â€” does NOT update idle timer
app.MapGet("/logs", (StartupLogStore logStore) =>
    Results.Ok(new
    {
        startup = "current",
        entries = logStore.GetEntries()
    }));

app.Run();
