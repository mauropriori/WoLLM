using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WoLLM.Config;

namespace WoLLM.Orchestration;

public interface IBackendProcessResolver
{
    BackendProcessResolution Resolve(ModelConfig model, ManagedProcessLaunch launch);
}

public enum BackendProcessResolutionKind
{
    Found,
    Missing,
    ForeignOwner
}

public sealed record BackendProcessResolution(
    BackendProcessResolutionKind Kind,
    IManagedProcessHandle? Process,
    DateTimeOffset? ProcessStartedAtUtc,
    int? OwnerProcessId,
    string Reason)
{
    public bool IsTrackedProcess => Kind == BackendProcessResolutionKind.Found && Process is not null;

    public static BackendProcessResolution Found(
        IManagedProcessHandle process,
        DateTimeOffset? startedAtUtc,
        string reason) =>
        new(BackendProcessResolutionKind.Found, process, startedAtUtc, process.Id, reason);

    public static BackendProcessResolution Missing(string reason) =>
        new(BackendProcessResolutionKind.Missing, null, null, null, reason);

    public static BackendProcessResolution ForeignOwner(int ownerProcessId, string reason) =>
        new(BackendProcessResolutionKind.ForeignOwner, null, null, ownerProcessId, reason);
}

public sealed class BackendProcessResolver : IBackendProcessResolver
{
    public BackendProcessResolution Resolve(ModelConfig model, ManagedProcessLaunch launch)
    {
        var ownerProcessIds = GetListeningOwnerProcessIds(model.Port);
        if (ownerProcessIds.Count == 0)
        {
            return BackendProcessResolution.Missing(
                $"No listening process owns port {model.Port} for model '{model.Name}'.");
        }

        foreach (var ownerProcessId in ownerProcessIds)
        {
            if (!IsSameOrDescendantProcess(ownerProcessId, launch.LauncherProcess.Id))
                continue;

            try
            {
                var process = ownerProcessId == launch.LauncherProcess.Id
                    ? launch.LauncherProcess
                    : new SystemManagedProcessHandle(Process.GetProcessById(ownerProcessId));

                return BackendProcessResolution.Found(
                    process,
                    process.StartTimeUtc,
                    ownerProcessId == launch.LauncherProcess.Id
                        ? $"Launcher PID {ownerProcessId} owns port {model.Port}."
                        : $"Descendant PID {ownerProcessId} owns port {model.Port}.");
            }
            catch (ArgumentException)
            {
                // The port owner exited between inspection and handle creation; keep scanning.
            }
            catch (InvalidOperationException)
            {
                // Process information is no longer available; keep scanning.
            }
        }

        return BackendProcessResolution.ForeignOwner(
            ownerProcessIds[0],
            $"Port {model.Port} is owned by PID {ownerProcessIds[0]}, which is outside launcher PID {launch.LauncherProcess.Id}.");
    }

    private static List<int> GetListeningOwnerProcessIds(int port)
    {
        var owners = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsListeningOwnerProcessIds(port)
            : GetLinuxListeningOwnerProcessIds(port);

        return owners
            .Distinct()
            .OrderBy(pid => pid)
            .ToList();
    }

    private static bool IsSameOrDescendantProcess(int processId, int ancestorProcessId)
    {
        if (processId == ancestorProcessId)
            return true;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? IsWindowsDescendantProcess(processId, ancestorProcessId)
            : IsLinuxDescendantProcess(processId, ancestorProcessId);
    }

    private static List<int> GetWindowsListeningOwnerProcessIds(int port)
    {
        var owners = new List<int>();
        owners.AddRange(GetWindowsListeningOwnerProcessIds<MibTcpRowOwnerPid>(port, AddressFamily.InterNetwork));
        owners.AddRange(GetWindowsListeningOwnerProcessIds<MibTcp6RowOwnerPid>(port, AddressFamily.InterNetworkV6));
        return owners;
    }

    private static IEnumerable<int> GetWindowsListeningOwnerProcessIds<TRow>(int port, AddressFamily addressFamily)
        where TRow : struct
    {
        var table = ReadWindowsTcpTable<TRow>(addressFamily);
        foreach (var row in table)
        {
            var listeningState = row switch
            {
                MibTcpRowOwnerPid ipv4 => ipv4.State == (uint)MibTcpState.Listen && ConvertPort(ipv4.LocalPort) == port,
                MibTcp6RowOwnerPid ipv6 => ipv6.State == (uint)MibTcpState.Listen && ConvertPort(ipv6.LocalPort) == port,
                _ => false
            };

            if (!listeningState)
                continue;

            yield return row switch
            {
                MibTcpRowOwnerPid ipv4 => checked((int)ipv4.OwningPid),
                MibTcp6RowOwnerPid ipv6 => checked((int)ipv6.OwningPid),
                _ => 0
            };
        }
    }

    private static List<TRow> ReadWindowsTcpTable<TRow>(AddressFamily addressFamily)
        where TRow : struct
    {
        const int afInet = 2;
        const int afInet6 = 23;

        var tableClass = TcpTableClass.OwnerPidAll;
        var addressFamilyCode = addressFamily == AddressFamily.InterNetwork ? afInet : afInet6;
        var bufferLength = 0;

        var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, true, addressFamilyCode, tableClass, 0);
        if (result != ErrorInsufficientBuffer)
            return [];

        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferLength, true, addressFamilyCode, tableClass, 0);
            if (result != 0)
                return [];

            var rowCount = Marshal.ReadInt32(buffer);
            var rows = new List<TRow>(rowCount);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<TRow>();

            for (var i = 0; i < rowCount; i++)
            {
                rows.Add(Marshal.PtrToStructure<TRow>(rowPointer));
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsWindowsDescendantProcess(int processId, int ancestorProcessId)
    {
        var parentMap = GetWindowsParentProcessMap();
        var current = processId;

        while (parentMap.TryGetValue(current, out var parentProcessId) && parentProcessId > 0 && parentProcessId != current)
        {
            if (parentProcessId == ancestorProcessId)
                return true;

            current = parentProcessId;
        }

        return false;
    }

    private static Dictionary<int, int> GetWindowsParentProcessMap()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
            return [];

        try
        {
            var entries = new Dictionary<int, int>();
            var processEntry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32First(snapshot, ref processEntry))
                return entries;

            do
            {
                entries[checked((int)processEntry.Th32ProcessId)] = checked((int)processEntry.Th32ParentProcessId);
                processEntry.DwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref processEntry));

            return entries;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static List<int> GetLinuxListeningOwnerProcessIds(int port)
    {
        var socketInodes = GetLinuxListeningSocketInodes(port);
        if (socketInodes.Count == 0)
            return [];

        var owners = new HashSet<int>();
        foreach (var procDirectory in Directory.EnumerateDirectories("/proc"))
        {
            var processName = Path.GetFileName(procDirectory);
            if (!int.TryParse(processName, out var processId))
                continue;

            var fdDirectory = Path.Combine(procDirectory, "fd");
            if (!Directory.Exists(fdDirectory))
                continue;

            try
            {
                foreach (var fdPath in Directory.EnumerateFiles(fdDirectory))
                {
                    string? target;
                    try
                    {
                        var fdInfo = new FileInfo(fdPath);
                        target = fdInfo.LinkTarget;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(target))
                        continue;

                    var inode = ParseLinuxSocketInode(target);
                    if (inode is not null && socketInodes.Contains(inode.Value))
                    {
                        owners.Add(processId);
                        break;
                    }
                }
            }
            catch
            {
                // Ignore transient or unauthorized /proc entries.
            }
        }

        return owners.OrderBy(pid => pid).ToList();
    }

    private static HashSet<long> GetLinuxListeningSocketInodes(int port)
    {
        var inodes = new HashSet<long>();
        AddLinuxListeningSocketInodes("/proc/net/tcp", port, inodes);
        AddLinuxListeningSocketInodes("/proc/net/tcp6", port, inodes);
        return inodes;
    }

    private static void AddLinuxListeningSocketInodes(string path, int port, HashSet<long> inodes)
    {
        if (!File.Exists(path))
            return;

        try
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 10)
                    continue;

                if (!string.Equals(parts[3], "0A", StringComparison.OrdinalIgnoreCase))
                    continue;

                var localAddress = parts[1];
                var separatorIndex = localAddress.LastIndexOf(':');
                if (separatorIndex < 0)
                    continue;

                var portHex = localAddress[(separatorIndex + 1)..];
                if (!int.TryParse(portHex, global::System.Globalization.NumberStyles.HexNumber, null, out var parsedPort))
                    continue;

                if (parsedPort != port)
                    continue;

                if (long.TryParse(parts[9], out var inode))
                    inodes.Add(inode);
            }
        }
        catch
        {
            // Ignore transient /proc parsing issues.
        }
    }

    private static long? ParseLinuxSocketInode(string linkTarget)
    {
        if (!linkTarget.StartsWith("socket:[", StringComparison.Ordinal) ||
            !linkTarget.EndsWith(']'))
        {
            return null;
        }

        var value = linkTarget["socket:[".Length..^1];
        return long.TryParse(value, out var inode) ? inode : null;
    }

    private static bool IsLinuxDescendantProcess(int processId, int ancestorProcessId)
    {
        var current = processId;
        while (current > 0 && current != ancestorProcessId)
        {
            var statusPath = $"/proc/{current}/status";
            if (!File.Exists(statusPath))
                return false;

            try
            {
                var parentLine = File.ReadLines(statusPath)
                    .FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));

                if (parentLine is null)
                    return false;

                var parts = parentLine.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var parentProcessId) || parentProcessId <= 0)
                    return false;

                if (parentProcessId == ancestorProcessId)
                    return true;

                if (parentProcessId == current)
                    return false;

                current = parentProcessId;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static int ConvertPort(uint port) => (ushort)IPAddress.NetworkToHostOrder((short)port);

    private const uint ErrorInsufficientBuffer = 122;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool sort,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum TcpTableClass
    {
        BasicListener,
        BasicConnections,
        BasicAll,
        OwnerPidListener,
        OwnerPidConnections,
        OwnerPidAll
    }

    private enum MibTcpState : uint
    {
        Closed = 1,
        Listen = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessId;
        public IntPtr Th32DefaultHeapId;
        public uint Th32ModuleId;
        public uint CntThreads;
        public uint Th32ParentProcessId;
        public int PcPriClassBase;
        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }
}
