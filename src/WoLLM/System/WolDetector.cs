using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WoLLM.System;

/// <summary>
/// Detects whether the machine was started (or woken from sleep/hibernate) via Wake-on-LAN.
/// The check runs once at startup and the result is cached.
/// Returns null if the platform does not support detection or the check fails.
/// </summary>
public static class WolDetector
{
    private static bool _checked;
    private static bool? _result;
    private static readonly object _lock = new();

    public static async Task<bool?> WasWolBootAsync()
    {
        lock (_lock)
        {
            if (_checked) return _result;
        }

        var result = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await DetectWindowsAsync()
            : await DetectLinuxAsync();

        lock (_lock)
        {
            _result  = result;
            _checked = true;
        }

        return result;
    }

    // ── Windows ───────────────────────────────────────────────────────────────
    // powercfg -lastwake reports the last wake source device.
    // When WOL triggered the wake the device name includes network-related keywords
    // (Ethernet, Network, LAN, NDIS, etc.) which are hardware strings and not localised.
    private static async Task<bool?> DetectWindowsAsync()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "powercfg",
                    Arguments              = "-lastwake",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };

            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ContainsNetworkKeyword(output);
        }
        catch { return null; }
    }

    // ── Linux ─────────────────────────────────────────────────────────────────
    // Try journalctl first, fall back to dmesg.
    // Both can contain WOL-related entries when the kernel logs the wake source.
    private static async Task<bool?> DetectLinuxAsync()
    {
        var journalResult = await TryJournalctlAsync();
        if (journalResult.HasValue) return journalResult;

        return await TryDmesgAsync();
    }

    private static async Task<bool?> TryJournalctlAsync()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "journalctl",
                    Arguments              = "-b 0 -k --no-pager --output=short",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };

            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ContainsWolKernelEntry(output);
        }
        catch { return null; }
    }

    private static async Task<bool?> TryDmesgAsync()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "dmesg",
                    Arguments              = "--notime",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };

            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ContainsWolKernelEntry(output);
        }
        catch { return null; }
    }

    // ── Keyword matching ──────────────────────────────────────────────────────

    // Hardware names and NDIS driver strings are English regardless of OS locale.
    private static readonly string[] NetworkKeywords =
    [
        "ethernet", "network", " lan", "ndis", "wifi", "wireless", "wlan", "wake-on"
    ];

    private static bool ContainsNetworkKeyword(string text)
    {
        var lower = text.ToLowerInvariant();
        return NetworkKeywords.Any(lower.Contains);
    }

    // Kernel messages related to WOL wake events.
    private static readonly string[] WolKernelKeywords =
    [
        "magic packet", "wol", "wake-on-lan", "wake on lan",
        "pm: wakeup", "acpi: waking", "wake source"
    ];

    private static bool ContainsWolKernelEntry(string text)
    {
        var lower = text.ToLowerInvariant();
        return WolKernelKeywords.Any(kw => lower.Contains(kw));
    }
}
