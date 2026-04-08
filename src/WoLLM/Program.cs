using Serilog;
using WoLLM.Config;
using WoLLM.Orchestration;
using WoLLM.System;

// Bootstrap logger so config errors are printed cleanly before DI is ready.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((_, services, logConfig) =>
    logConfig
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.File("logs/wollm-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7));

// Load and validate config — exits with code 1 on any error.
// Use a temporary MEL logger backed by Serilog for early startup.
using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSerilog());
var config = ConfigLoader.Load(bootstrapLoggerFactory.CreateLogger(nameof(ConfigLoader)));

// Register services.
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ModelOrchestrator>();
builder.Services.AddSingleton<IdleWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());

// Short timeout per health-check probe; failures are retried every second by the orchestrator.
builder.Services.AddHttpClient("healthcheck", client =>
    client.Timeout = TimeSpan.FromSeconds(5));

// Bind to configured port on all interfaces.
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(config.Port));

var app = builder.Build();
app.UseSerilogRequestLogging();

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
var watchdog     = app.Services.GetRequiredService<IdleWatchdog>();

// ── Endpoints ────────────────────────────────────────────────────────────────

// GET /health — intentionally does NOT update idle timer (keepalives must not prevent shutdown).
app.MapGet("/health", () =>
    Results.Ok(new
    {
        status       = "ok",
        currentModel = orchestrator.CurrentModel?.Name
    }));

// POST /session/start?shutdown_on_idle=true|false
app.MapPost("/session/start", (bool? shutdown_on_idle) =>
{
    watchdog.SetShutdownOnIdle(shutdown_on_idle ?? false);
    watchdog.RecordActivity();
    return Results.Ok(new
    {
        status         = "ok",
        shutdownOnIdle = watchdog.ShutdownOnIdle
    });
});

// POST /switch/{modelName}
app.MapPost("/switch/{modelName}", async (string modelName, CancellationToken ct) =>
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
    var sysTask = SystemStats.GetAsync();
    var wolTask = WolDetector.WasWolBootAsync();
    await Task.WhenAll(sysTask, wolTask);

    var sys = sysTask.Result;
    return Results.Ok(new
    {
        currentModel       = orchestrator.CurrentModel?.Name,
        shutdownOnIdle     = watchdog.ShutdownOnIdle,
        idleTimeoutMinutes = config.IdleTimeoutMinutes,
        idleSeconds        = (int)watchdog.IdleFor.TotalSeconds,
        wolBoot            = wolTask.Result,
        system = new
        {
            cpuPercent  = sys.CpuPercent,
            ramUsedMb   = sys.RamUsedMb,
            ramTotalMb  = sys.RamTotalMb,
            gpuPercent  = sys.GpuPercent,
            vramUsedMb  = sys.VramUsedMb,
            vramTotalMb = sys.VramTotalMb
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

app.Run();
