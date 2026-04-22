namespace WoLLM.Logging;

public sealed class ManagedProcessLogSession
{
    private readonly Task _completion;

    private ManagedProcessLogSession(ProcessLogPaths paths, Task completion)
    {
        Paths = paths;
        _completion = completion;
    }

    public ProcessLogPaths Paths { get; }
    public Task Completion => _completion;

    public static ManagedProcessLogSession Start(
        string modelName,
        int processId,
        Stream stdout,
        Stream stderr)
    {
        var paths = CreatePaths(modelName, processId);
        var stdoutTask = PumpAsync(stdout, paths.StdoutPath);
        var stderrTask = PumpAsync(stderr, paths.StderrPath);

        return new ManagedProcessLogSession(paths, Task.WhenAll(stdoutTask, stderrTask));
    }

    private static async Task PumpAsync(Stream source, string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var target = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await source.CopyToAsync(target);
        await target.FlushAsync();
    }

    private static ProcessLogPaths CreatePaths(string modelName, int pid)
    {
        var sanitizedModelName = SanitizePathSegment(modelName);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var filePrefix = $"{timestamp}-pid{pid}";
        var directory = Path.Combine("logs", "processes", sanitizedModelName);

        return new ProcessLogPaths(
            StdoutPath: Path.Combine(directory, $"{filePrefix}-stdout.log"),
            StderrPath: Path.Combine(directory, $"{filePrefix}-stderr.log"));
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = value.Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            return "unknown-model";

        var buffer = sanitized.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(invalidChars, buffer[i]) >= 0)
                buffer[i] = '_';
        }

        return new string(buffer);
    }
}

public sealed record ProcessLogPaths(string StdoutPath, string StderrPath);
