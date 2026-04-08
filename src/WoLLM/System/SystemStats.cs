using System.Diagnostics;
using System.Runtime.InteropServices;
using CultureInfo  = global::System.Globalization.CultureInfo;
using NumberStyles = global::System.Globalization.NumberStyles;
using Text         = global::System.Text;

namespace WoLLM.System;

public record CpuInfo(string Name, double UsagePercent);
public record GpuInfo(string Name, double? UsagePercent, long? VramUsedMb, long? VramTotalMb);

public record SystemSnapshot(
    CpuInfo[] Cpus,
    long      RamUsedMb,
    long      RamTotalMb,
    GpuInfo[] Gpus);

public static class SystemStats
{
    public static async Task<SystemSnapshot> GetAsync()
    {
        var cpuTask = GetCpusAsync();
        var ramTask = GetRamAsync();
        var gpuTask = GetGpusAsync();

        await Task.WhenAll(cpuTask, ramTask, gpuTask);

        var (ramUsed, ramTotal) = ramTask.Result;
        return new SystemSnapshot(cpuTask.Result, ramUsed, ramTotal, gpuTask.Result);
    }

    // ── CPU ───────────────────────────────────────────────────────────────────

    private static async Task<CpuInfo[]> GetCpusAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await GetCpusWindowsAsync();

        return await GetCpusLinuxAsync();
    }

    // Windows: Win32_Processor gives per-socket Name + LoadPercentage.
    private static async Task<CpuInfo[]> GetCpusWindowsAsync()
    {
        try
        {
            const string script = """
                Get-CimInstance Win32_Processor | ForEach-Object { "$($_.Name)|$($_.LoadPercentage)" }
                """;
            var encoded = Convert.ToBase64String(Text.Encoding.Unicode.GetBytes(script));
            var output  = await RunProcessAsync("powershell",
                $"-NoProfile -NonInteractive -EncodedCommand {encoded}");

            if (!string.IsNullOrWhiteSpace(output))
            {
                var cpus = output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line =>
                    {
                        var sep = line.LastIndexOf('|');
                        if (sep < 0) return null;
                        var name  = line[..sep].Trim();
                        double.TryParse(line[(sep + 1)..].Trim(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
                        return new CpuInfo(name, Math.Round(pct, 1));
                    })
                    .Where(c => c != null)
                    .ToArray();

                if (cpus.Length > 0) return cpus!;
            }
        }
        catch { }

        return [];
    }

    // Linux: /proc/cpuinfo for names (grouped by physical id), /proc/stat for total usage.
    private static async Task<CpuInfo[]> GetCpusLinuxAsync()
    {
        try
        {
            // Measure CPU usage before reading cpuinfo to avoid blocking the delay.
            var (idle1, total1) = ReadProcStat();
            var cpuInfoTask = File.ReadAllLinesAsync("/proc/cpuinfo");
            await Task.Delay(300);
            var (idle2, total2) = ReadProcStat();

            double totalUsage = 0;
            var totalDelta = total2 - total1;
            var idleDelta  = idle2  - idle1;
            if (totalDelta > 0)
                totalUsage = Math.Round((1.0 - (double)idleDelta / totalDelta) * 100.0, 1);

            // Extract one model name per unique physical id.
            var cpuInfoLines = await cpuInfoTask;
            var bySocket = new Dictionary<string, string>(); // physicalId → modelName
            string? currentPhysId = null;
            string? currentName   = null;

            foreach (var line in cpuInfoLines)
            {
                if (line.StartsWith("physical id"))
                    currentPhysId = line.Split(':')[1].Trim();
                else if (line.StartsWith("model name"))
                    currentName = line.Split(':', 2)[1].Trim();

                if (currentPhysId != null && currentName != null)
                {
                    bySocket.TryAdd(currentPhysId, currentName);
                    currentPhysId = null;
                    currentName   = null;
                }
            }

            // Fallback: if /proc/cpuinfo has no physical id (VMs, some ARM), use first model name.
            if (bySocket.Count == 0)
            {
                var first = cpuInfoLines.FirstOrDefault(l => l.StartsWith("model name"));
                if (first != null)
                    bySocket["0"] = first.Split(':', 2)[1].Trim();
            }

            // All sockets share the same total-system usage figure (per-socket breakdown
            // would require correlating /proc/stat per-cpu lines with topology, out of scope).
            return bySocket.Values
                .Select(name => new CpuInfo(name, totalUsage))
                .ToArray();
        }
        catch { return []; }
    }

    private static (long idle, long total) ReadProcStat()
    {
        var firstLine = File.ReadLines("/proc/stat").First();
        // cpu  user nice system idle iowait irq softirq steal guest guest_nice
        var parts  = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long idle   = long.Parse(parts[4]);
        long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
        long total  = parts.Skip(1).Sum(long.Parse);
        return (idle + iowait, total);
    }

    // ── RAM ───────────────────────────────────────────────────────────────────

    private static async Task<(long used, long total)> GetRamAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetRamWindows();

        return await GetRamLinuxAsync();
    }

    private static (long used, long total) GetRamWindows()
    {
        try
        {
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                var total = (long)(mem.ullTotalPhys / 1024 / 1024);
                var used  = total - (long)(mem.ullAvailPhys / 1024 / 1024);
                return (used, total);
            }
        }
        catch { }

        return (0, 0);
    }

    private static async Task<(long used, long total)> GetRamLinuxAsync()
    {
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/meminfo");
            long memTotal = 0, memAvail = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("MemTotal:"))
                    memTotal = ParseMemInfoKb(line);
                else if (line.StartsWith("MemAvailable:"))
                    memAvail = ParseMemInfoKb(line);
            }
            return ((memTotal - memAvail) / 1024, memTotal / 1024);
        }
        catch { return (0, 0); }
    }

    private static long ParseMemInfoKb(string line)
    {
        var parts = line.Split(':', StringSplitOptions.TrimEntries);
        return long.Parse(parts[1].Split(' ')[0]);
    }

    // ── GPU ───────────────────────────────────────────────────────────────────

    private static async Task<GpuInfo[]> GetGpusAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await GetGpusWindowsAsync();

        return await GetGpusLinuxAsync();
    }

    // Windows: nvidia-smi first (NVIDIA), then WDDM counters (AMD/Intel/any).
    private static async Task<GpuInfo[]> GetGpusWindowsAsync()
    {
        var nvidia = await TryNvidiaSmiAsync();
        if (nvidia.Length > 0) return nvidia;

        return await TryWddmCountersAsync();
    }

    // Linux: nvidia-smi → rocm-smi → radeontop → AMD sysfs → Intel sysfs.
    private static async Task<GpuInfo[]> GetGpusLinuxAsync()
    {
        var nvidia = await TryNvidiaSmiAsync();
        if (nvidia.Length > 0) return nvidia;

        var rocm = await TryRocmSmiAsync();
        if (rocm.Length > 0) return rocm;

        var radeon = await TryRadeontopAsync();
        if (radeon.Length > 0) return radeon;

        var amd = await TryAmdSysfsAsync();
        if (amd.Length > 0) return amd;

        return await TryIntelSysfsAsync();
    }

    // nvidia-smi — supports multi-GPU, works on Windows and Linux.
    private static async Task<GpuInfo[]> TryNvidiaSmiAsync()
    {
        try
        {
            var output = await RunProcessAsync("nvidia-smi",
                "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits");
            if (string.IsNullOrWhiteSpace(output)) return [];

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split(',');
                    if (parts.Length < 4) return null;
                    var name = parts[0].Trim();
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
                    long.TryParse(parts[2].Trim(), out var vramUsed);
                    long.TryParse(parts[3].Trim(), out var vramTotal);
                    return new GpuInfo(name, pct, vramUsed, vramTotal);
                })
                .Where(g => g != null)
                .ToArray()!;
        }
        catch { return []; }
    }

    // rocm-smi — AMD with ROCm driver, Linux only.
    private static async Task<GpuInfo[]> TryRocmSmiAsync()
    {
        try
        {
            var output = await RunProcessAsync("rocm-smi", "--showuse --showmeminfo vram --noheader");
            if (string.IsNullOrWhiteSpace(output)) return [];

            // Collect per-GPU data keyed by GPU index ("GPU[0]", "GPU[1]", ...).
            var gpuData = new Dictionary<string, (double? pct, long? used, long? total)>();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;
                var key  = line[..colonIdx].Trim();   // "GPU[0]"
                var rest = line[(colonIdx + 1)..].Trim();

                if (!gpuData.ContainsKey(key)) gpuData[key] = (null, null, null);
                var (pct, used, total) = gpuData[key];

                if (rest.StartsWith("GPU use (%):") &&
                    double.TryParse(rest["GPU use (%):".Length..].Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    pct = p;

                if (rest.StartsWith("VRAM Total Used Memory (B):") &&
                    long.TryParse(rest["VRAM Total Used Memory (B):".Length..].Trim(), out var u))
                    used = u / 1024 / 1024;

                if (rest.StartsWith("VRAM Total Memory (B):") &&
                    long.TryParse(rest["VRAM Total Memory (B):".Length..].Trim(), out var t))
                    total = t / 1024 / 1024;

                gpuData[key] = (pct, used, total);
            }

            return gpuData
                .Where(kv => kv.Value.pct.HasValue)
                .Select(kv => new GpuInfo(kv.Key, kv.Value.pct, kv.Value.used, kv.Value.total))
                .ToArray();
        }
        catch { return []; }
    }

    // radeontop — AMD Linux (amdgpu/radeon, Vulkan-layer aware). Single GPU only.
    // "radeontop -d - -l 1" sample line:
    // "1748000000.000000: bus 06, gpu 5.00%, ee 0.00%, ... vram 12.50% 512.00mb/4096.00mb, ..."
    private static async Task<GpuInfo[]> TryRadeontopAsync()
    {
        try
        {
            var output = await RunProcessAsync("radeontop", "-d - -l 1 --colour 0");
            if (string.IsNullOrWhiteSpace(output)) return [];

            double? gpuPct = null;
            long? vramUsed = null, vramTotal = null;

            foreach (var line in output.Split('\n'))
            {
                var gpuIdx = line.IndexOf("gpu ", StringComparison.OrdinalIgnoreCase);
                if (gpuIdx >= 0 && gpuPct == null)
                {
                    var after  = line[(gpuIdx + 4)..].TrimStart();
                    var pctEnd = after.IndexOf('%');
                    if (pctEnd > 0 && double.TryParse(after[..pctEnd].Trim(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        gpuPct = pct;
                }

                var vramIdx = line.IndexOf("vram ", StringComparison.OrdinalIgnoreCase);
                if (vramIdx >= 0 && vramUsed == null)
                {
                    var after  = line[(vramIdx + 5)..].TrimStart();
                    var pctEnd = after.IndexOf('%');
                    if (pctEnd > 0)
                    {
                        after = after[(pctEnd + 1)..].TrimStart();
                        var slash = after.IndexOf('/');
                        if (slash > 0)
                        {
                            var usedStr  = after[..slash].Trim().TrimEnd('m', 'b', 'M', 'B');
                            var totalStr = after[(slash + 1)..].Trim().TrimEnd('m', 'b', 'M', 'B');
                            if (double.TryParse(usedStr,  NumberStyles.Float, CultureInfo.InvariantCulture, out var u) &&
                                double.TryParse(totalStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                            {
                                vramUsed  = (long)u;
                                vramTotal = (long)t;
                            }
                        }
                    }
                }
            }

            if (!gpuPct.HasValue) return [];

            // radeontop doesn't easily expose the GPU name; read it from sysfs if possible.
            var name = await ReadAmdGpuNameFromSysfsAsync() ?? "AMD GPU";
            return [new GpuInfo(name, gpuPct, vramUsed, vramTotal)];
        }
        catch { return []; }
    }

    // AMD sysfs — one entry per card that exposes gpu_busy_percent.
    private static async Task<GpuInfo[]> TryAmdSysfsAsync()
    {
        try
        {
            var cards = Directory.GetDirectories("/sys/class/drm", "card*")
                .Where(d => !Path.GetFileName(d).Contains('-'))
                .OrderBy(d => d)
                .ToArray();

            var results = new List<GpuInfo>();
            foreach (var card in cards)
            {
                var busyPath  = Path.Combine(card, "device", "gpu_busy_percent");
                if (!File.Exists(busyPath)) continue;

                var busy  = double.Parse((await File.ReadAllTextAsync(busyPath)).Trim());
                long? used  = null, total = null;

                var usedPath  = Path.Combine(card, "device", "mem_info_vram_used");
                var totalPath = Path.Combine(card, "device", "mem_info_vram_total");
                if (File.Exists(usedPath))
                    used  = long.Parse((await File.ReadAllTextAsync(usedPath)).Trim()) / 1024 / 1024;
                if (File.Exists(totalPath))
                    total = long.Parse((await File.ReadAllTextAsync(totalPath)).Trim()) / 1024 / 1024;

                // Try to read GPU name from uevent (contains DRIVER and PCI info).
                var name = await ReadGpuNameFromUeventAsync(Path.Combine(card, "device")) ?? "AMD GPU";
                results.Add(new GpuInfo(name, busy, used, total));
            }

            return results.ToArray();
        }
        catch { return []; }
    }

    // Intel sysfs — single entry, utilization only via engine busy delta.
    private static async Task<GpuInfo[]> TryIntelSysfsAsync()
    {
        try
        {
            var engineFiles = Directory
                .GetFiles("/sys/class/drm", "engine_busy_ms", SearchOption.AllDirectories)
                .Where(f => f.Contains("rcs"))
                .ToArray();

            if (engineFiles.Length == 0) return [];

            var engineFile = engineFiles[0];
            var t1 = long.Parse((await File.ReadAllTextAsync(engineFile)).Trim());
            await Task.Delay(300);
            var t2 = long.Parse((await File.ReadAllTextAsync(engineFile)).Trim());

            var pct  = Math.Round(Math.Min((t2 - t1) / 3.0, 100.0), 1);
            var card = engineFile.Split("/sys/class/drm/").LastOrDefault()?.Split('/').FirstOrDefault();
            var name = (card != null
                ? await ReadGpuNameFromUeventAsync($"/sys/class/drm/{card}/device")
                : null) ?? "Intel GPU";

            return [new GpuInfo(name, pct, null, null)];
        }
        catch { return []; }
    }

    // Windows WDDM Performance Counters — AMD, Intel, any WDDM GPU.
    // Outputs one line per adapter: "Name|gpuPct|vramUsedBytes|adapterRAMBytes"
    private static async Task<GpuInfo[]> TryWddmCountersAsync()
    {
        try
        {
            const string script = """
                $gpuSamples  = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples
                $memSamples  = (Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage'          -ErrorAction SilentlyContinue).CounterSamples
                $controllers = Get-CimInstance Win32_VideoController

                # Group engine samples by phys index embedded in instance name.
                $gpuByPhys = @{}
                foreach ($s in $gpuSamples) {
                    if ($s.InstanceName -match 'phys_(\d+)') {
                        $p = $Matches[1]
                        if (-not $gpuByPhys[$p]) { $gpuByPhys[$p] = 0.0 }
                        $gpuByPhys[$p] += $s.CookedValue
                    }
                }
                $memByPhys = @{}
                foreach ($s in $memSamples) {
                    if ($s.InstanceName -match 'phys_(\d+)') {
                        $p = $Matches[1]
                        if (-not $memByPhys[$p]) { $memByPhys[$p] = 0.0 }
                        $memByPhys[$p] += $s.CookedValue
                    }
                }

                for ($i = 0; $i -lt $controllers.Count; $i++) {
                    $c    = $controllers[$i]
                    $g    = if ($gpuByPhys.ContainsKey("$i")) { $gpuByPhys["$i"] } else { 0 }
                    $m    = if ($memByPhys.ContainsKey("$i")) { $memByPhys["$i"] } else { 0 }
                    $ram  = $c.AdapterRAM
                    "$($c.Name)|$g|$m|$ram"
                }
                """;

            var encoded = Convert.ToBase64String(Text.Encoding.Unicode.GetBytes(script));
            var output  = await RunProcessAsync("powershell",
                $"-NoProfile -NonInteractive -EncodedCommand {encoded}");

            if (string.IsNullOrWhiteSpace(output)) return [];

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Trim().Split('|');
                    if (parts.Length < 4) return null;
                    var name = parts[0].Trim();
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
                    double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vramUsedBytes);
                    double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vramTotalBytes);
                    return new GpuInfo(
                        name,
                        Math.Round(Math.Min(pct, 100.0), 1),
                        vramUsedBytes  > 0 ? (long)(vramUsedBytes  / 1024 / 1024) : null,
                        vramTotalBytes > 0 ? (long)(vramTotalBytes / 1024 / 1024) : null);
                })
                .Where(g => g != null)
                .ToArray()!;
        }
        catch { return []; }
    }

    // ── Sysfs name helpers (Linux) ─────────────────────────────────────────────

    // Reads "AMD Radeon RX 6700 XT" style name from /sys/class/drm/card*/device/uevent.
    private static async Task<string?> ReadGpuNameFromUeventAsync(string devicePath)
    {
        try
        {
            var ueventPath = Path.Combine(devicePath, "uevent");
            if (!File.Exists(ueventPath)) return null;

            foreach (var line in await File.ReadAllLinesAsync(ueventPath))
            {
                // PCI_ID or DRIVER line — not useful for a human-readable name.
                // Some drivers expose "gpu_name" in product or subsystem files.
            }

            // Try modalias or product name files.
            foreach (var candidate in new[] { "product_name", "label" })
            {
                var p = Path.Combine(devicePath, candidate);
                if (File.Exists(p)) return (await File.ReadAllTextAsync(p)).Trim();
            }

            // Fall back to PCI subsystem name if available.
            var subsysName = Path.Combine(devicePath, "subsystem_device");
            if (File.Exists(subsysName))
                return (await File.ReadAllTextAsync(subsysName)).Trim();

            return null;
        }
        catch { return null; }
    }

    private static Task<string?> ReadAmdGpuNameFromSysfsAsync()
    {
        try
        {
            var card = Directory.GetDirectories("/sys/class/drm", "card*")
                .Where(d => !Path.GetFileName(d).Contains('-'))
                .OrderBy(d => d)
                .FirstOrDefault();

            return card != null
                ? ReadGpuNameFromUeventAsync(Path.Combine(card, "device"))
                : Task.FromResult<string?>(null);
        }
        catch { return Task.FromResult<string?>(null); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string?> RunProcessAsync(string fileName, string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            }
        };

        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? output : null;
    }

    // ── P/Invoke (Windows RAM) ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint  dwLength       = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
