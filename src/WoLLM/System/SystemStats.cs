using System.Diagnostics;
using System.Runtime.InteropServices;
using CultureInfo  = global::System.Globalization.CultureInfo;
using NumberStyles = global::System.Globalization.NumberStyles;

namespace WoLLM.System;

public record SystemSnapshot(
    double  CpuPercent,
    long    RamUsedMb,
    long    RamTotalMb,
    double? GpuPercent,
    long?   VramUsedMb,
    long?   VramTotalMb);

public static class SystemStats
{
    public static async Task<SystemSnapshot> GetAsync()
    {
        // CPU and RAM can be sampled in parallel with GPU probing.
        var cpuRamTask = GetCpuAndRamAsync();
        var gpuTask    = GetGpuAsync();

        await Task.WhenAll(cpuRamTask, gpuTask);

        var (cpu, ramUsed, ramTotal) = cpuRamTask.Result;
        var (gpuPct, vramUsed, vramTotal) = gpuTask.Result;

        return new SystemSnapshot(cpu, ramUsed, ramTotal, gpuPct, vramUsed, vramTotal);
    }

    // ── CPU + RAM ─────────────────────────────────────────────────────────────

    private static async Task<(double cpu, long ramUsed, long ramTotal)> GetCpuAndRamAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await GetCpuAndRamWindowsAsync();

        return await GetCpuAndRamLinuxAsync();
    }

    // Windows — PowerShell for CPU, GlobalMemoryStatusEx for RAM.
    private static async Task<(double cpu, long ramUsed, long ramTotal)> GetCpuAndRamWindowsAsync()
    {
        double cpu = 0;
        try
        {
            // Get-Counter samples over 1 second and returns an accurate average.
            var output = await RunProcessAsync("powershell",
                "-NoProfile -NonInteractive -Command \"(Get-Counter '\\Processor Information(_Total)\\% Processor Time').CounterSamples[0].CookedValue\"");
            if (output != null && double.TryParse(output.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
                cpu = Math.Round(Math.Min(parsed, 100.0), 1);
        }
        catch { /* leave 0 */ }

        long ramUsed = 0, ramTotal = 0;
        try
        {
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                ramTotal = (long)(mem.ullTotalPhys / 1024 / 1024);
                ramUsed  = ramTotal - (long)(mem.ullAvailPhys / 1024 / 1024);
            }
        }
        catch { /* P/Invoke not available — leave 0 */ }

        return (cpu, ramUsed, ramTotal);
    }

    // Linux — /proc/stat for CPU, /proc/meminfo for RAM.
    private static async Task<(double cpu, long ramUsed, long ramTotal)> GetCpuAndRamLinuxAsync()
    {
        double cpu = 0;
        try
        {
            var (idle1, total1) = ReadProcStat();
            await Task.Delay(300);
            var (idle2, total2) = ReadProcStat();
            var totalDelta = total2 - total1;
            var idleDelta  = idle2  - idle1;
            if (totalDelta > 0)
                cpu = Math.Round((1.0 - (double)idleDelta / totalDelta) * 100.0, 1);
        }
        catch { }

        long ramUsed = 0, ramTotal = 0;
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
            ramTotal = memTotal / 1024;
            ramUsed  = (memTotal - memAvail) / 1024;
        }
        catch { }

        return (cpu, ramUsed, ramTotal);
    }

    private static (long idle, long total) ReadProcStat()
    {
        var firstLine = File.ReadLines("/proc/stat").First();
        // cpu  user nice system idle iowait irq softirq steal guest guest_nice
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long idle  = long.Parse(parts[4]);                   // idle + iowait
        long iowait = parts.Length > 5 ? long.Parse(parts[5]) : 0;
        long total = parts.Skip(1).Sum(long.Parse);
        return (idle + iowait, total);
    }

    private static long ParseMemInfoKb(string line)
    {
        // Format: "MemTotal:     16384000 kB"
        var parts = line.Split(':', StringSplitOptions.TrimEntries);
        return long.Parse(parts[1].Split(' ')[0]);
    }

    // ── GPU ───────────────────────────────────────────────────────────────────

    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> GetGpuAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await GetGpuWindowsAsync();

        return await GetGpuLinuxAsync();
    }

    // Windows: try nvidia-smi first, then PowerShell WDDM counters.
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> GetGpuWindowsAsync()
    {
        var nvidia = await TryNvidiaSmiAsync();
        if (nvidia.gpuPct.HasValue) return nvidia;

        return await TryWddmCountersAsync();
    }

    // Linux: try nvidia-smi → AMD sysfs → Intel sysfs.
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> GetGpuLinuxAsync()
    {
        var nvidia = await TryNvidiaSmiAsync();
        if (nvidia.gpuPct.HasValue) return nvidia;

        var amd = await TryAmdSysfsAsync();
        if (amd.gpuPct.HasValue) return amd;

        return await TryIntelSysfsAsync();
    }

    // nvidia-smi (NVIDIA, works on both Windows and Linux).
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> TryNvidiaSmiAsync()
    {
        try
        {
            var output = await RunProcessAsync("nvidia-smi",
                "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits");
            if (string.IsNullOrWhiteSpace(output)) return (null, null, null);

            var parts = output.Trim().Split(',');
            if (parts.Length < 3) return (null, null, null);

            return (
                double.Parse(parts[0].Trim()),
                long.Parse(parts[1].Trim()),
                long.Parse(parts[2].Trim()));
        }
        catch { return (null, null, null); }
    }

    // AMD sysfs (Linux only).
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> TryAmdSysfsAsync()
    {
        try
        {
            var cards = Directory.GetDirectories("/sys/class/drm", "card*")
                .Where(d => !Path.GetFileName(d).Contains('-'))
                .OrderBy(d => d)
                .ToArray();

            foreach (var card in cards)
            {
                var busyPath  = Path.Combine(card, "device", "gpu_busy_percent");
                var usedPath  = Path.Combine(card, "device", "mem_info_vram_used");
                var totalPath = Path.Combine(card, "device", "mem_info_vram_total");

                if (!File.Exists(busyPath)) continue;

                var busy  = double.Parse((await File.ReadAllTextAsync(busyPath)).Trim());
                long used = 0, total = 0;
                if (File.Exists(usedPath))
                    used = long.Parse((await File.ReadAllTextAsync(usedPath)).Trim()) / 1024 / 1024;
                if (File.Exists(totalPath))
                    total = long.Parse((await File.ReadAllTextAsync(totalPath)).Trim()) / 1024 / 1024;

                return (busy, used > 0 ? used : null, total > 0 ? total : null);
            }
        }
        catch { }

        return (null, null, null);
    }

    // Intel sysfs (Linux only, integrated GPU — utilization only via engine busy delta).
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> TryIntelSysfsAsync()
    {
        try
        {
            // Find the first render engine busy file.
            var engineFiles = Directory
                .GetFiles("/sys/class/drm", "engine_busy_ms", SearchOption.AllDirectories)
                .Where(f => f.Contains("rcs"))
                .ToArray();

            if (engineFiles.Length == 0) return (null, null, null);

            var engineFile = engineFiles[0];
            var t1   = long.Parse((await File.ReadAllTextAsync(engineFile)).Trim());
            await Task.Delay(300);
            var t2   = long.Parse((await File.ReadAllTextAsync(engineFile)).Trim());

            var deltaBusy = t2 - t1;
            var pct = Math.Round(Math.Min(deltaBusy / 3.0, 100.0), 1); // 300ms window → /3 = per-ms%
            return (pct, null, null);
        }
        catch { return (null, null, null); }
    }

    // Windows WDDM Performance Counters via PowerShell (AMD, Intel, any WDDM GPU).
    private static async Task<(double? gpuPct, long? vramUsed, long? vramTotal)> TryWddmCountersAsync()
    {
        try
        {
            // GPU utilization: sum all 3D engine utilization samples.
            const string psScript = @"
$gpu = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum | Select-Object -ExpandProperty Sum
$vramUsed = (Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum | Select-Object -ExpandProperty Sum
$vramTotal = (Get-WmiObject Win32_VideoController | Measure-Object AdapterRAM -Sum | Select-Object -ExpandProperty Sum)
Write-Output ""$gpu,$vramUsed,$vramTotal""
";
            var output = await RunProcessAsync("powershell",
                $"-NoProfile -NonInteractive -Command \"{psScript.Replace("\"", "\\\"").Replace(Environment.NewLine, " ")}\"");

            if (string.IsNullOrWhiteSpace(output)) return (null, null, null);

            var parts = output.Trim().Split(',');
            if (parts.Length < 3) return (null, null, null);

            double.TryParse(parts[0].Trim(), out var gpuPct);
            double.TryParse(parts[1].Trim(), out var vramUsedBytes);
            double.TryParse(parts[2].Trim(), out var vramTotalBytes);

            return (
                Math.Round(Math.Min(gpuPct, 100.0), 1),
                vramUsedBytes  > 0 ? (long)(vramUsedBytes  / 1024 / 1024) : null,
                vramTotalBytes > 0 ? (long)(vramTotalBytes / 1024 / 1024) : null);
        }
        catch { return (null, null, null); }
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
