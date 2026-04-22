using System.Diagnostics;

namespace WoLLM.Orchestration;

public interface IManagedProcessHandle : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    DateTimeOffset? StartTimeUtc { get; }
    Stream StandardOutputStream { get; }
    Stream StandardErrorStream { get; }
    int? TryGetExitCode();
    void Kill(bool entireProcessTree);
    Task WaitForExitAsync(CancellationToken ct = default);
}

public sealed class SystemManagedProcessHandle(Process process) : IManagedProcessHandle
{
    private readonly Process _process = process;

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public Stream StandardOutputStream => _process.StandardOutput.BaseStream;
    public Stream StandardErrorStream => _process.StandardError.BaseStream;

    public DateTimeOffset? StartTimeUtc
    {
        get
        {
            try
            {
                return _process.StartTime.ToUniversalTime();
            }
            catch
            {
                return null;
            }
        }
    }

    public int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    public void Dispose() => _process.Dispose();
}
