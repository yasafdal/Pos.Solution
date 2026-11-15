using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace POS.Shared.Helpers;

/// <summary>
/// Derives a deterministic, hardware-bound DeviceId that combines the host machine's
/// platform/OS identity (outside SQLite) with the terminal's DeviceUuid.
/// Copying local_pos.db to another physical machine produces a different live DeviceId,
/// immediately invalidating the copied license binding.
/// </summary>
public static class DeviceBindingHelper
{
    /// <summary>
    /// Optional provider hook for isolated automated test suites to simulate multiple physical machines.
    /// </summary>
    public static Func<string>? HardwareFingerprintProvider { get; set; }

    public static string GetHardwareFingerprint()
    {
        if (HardwareFingerprintProvider != null)
        {
            var overridden = HardwareFingerprintProvider();
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden.Trim();
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var regGuid = TryReadWindowsMachineGuid();
                if (!string.IsNullOrWhiteSpace(regGuid))
                {
                    return $"WIN|{regGuid.Trim().ToUpperInvariant()}|{Environment.MachineName.ToUpperInvariant()}";
                }
            }
            else if (File.Exists("/etc/machine-id"))
            {
                var machineId = File.ReadAllText("/etc/machine-id").Trim();
                if (!string.IsNullOrWhiteSpace(machineId))
                {
                    return $"LINUX|{machineId.ToUpperInvariant()}";
                }
            }
        }
        catch
        {
            // Fallback to OS/Host fingerprint below
        }

        return $"HOST|{Environment.MachineName.ToUpperInvariant()}|{RuntimeInformation.OSArchitecture}|{Environment.ProcessorCount}";
    }

    public static string ComputeDeviceId(string? deviceUuid, string? explicitHardwareFingerprint = null)
    {
        var cleanUuid = string.IsNullOrWhiteSpace(deviceUuid)
            ? "UNASSIGNED"
            : deviceUuid.Trim().ToLowerInvariant();
        var hw = !string.IsNullOrWhiteSpace(explicitHardwareFingerprint)
            ? explicitHardwareFingerprint.Trim()
            : GetHardwareFingerprint();

        var raw = $"POS-DEVICE-V1|{hw}|{cleanUuid}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hashBytes);
        return $"DEV-{hex[..24]}";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryReadWindowsMachineGuid()
    {
        try
        {
            // Read HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid using reflection or reg query safe path
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var val = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }
        catch
        {
            // Ignore registry access exceptions on non-Windows or restricted sandboxes
        }
        return null;
    }
}
