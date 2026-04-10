using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace WoLLM.Logging;

public sealed class StartupLogStore
{
    private const int MaxEntries = 1000;
    private readonly ConcurrentQueue<StartupLogEntry> _entries = new();

    public void Add(LogEvent logEvent)
    {
        _entries.Enqueue(new StartupLogEntry(
            Timestamp: logEvent.Timestamp.UtcDateTime,
            Level: logEvent.Level.ToString(),
            Message: logEvent.RenderMessage(),
            Exception: logEvent.Exception?.ToString()));

        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyCollection<StartupLogEntry> GetEntries() => _entries.ToArray();
}

public sealed record StartupLogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    string? Exception);

public sealed class StartupLogSink(StartupLogStore store) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => store.Add(logEvent);
}
