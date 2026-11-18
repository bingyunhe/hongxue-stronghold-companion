using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using ASPAssistant.Core.Models;
using MaaFramework.Binding;

namespace ASPAssistant.Core.Services;

public sealed record TcpListenerOwner(string Address, int Port, int ProcessId);

internal sealed record MuMuCaptureConfig(string Path, int Index, string? LibraryPath);

public static class EmulatorDiscovery
{
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public static IReadOnlyList<AdbDeviceInfo> FindDevices(string customAdbPath)
    {
        using var found = MaaToolkit.Shared.AdbDevice.Find();
        var devices = found.Select(Copy).ToList();
        if (File.Exists(customAdbPath) && devices.All(d => !SamePath(d.AdbPath, customAdbPath)))
        {
            using var custom = MaaToolkit.Shared.AdbDevice.Find(customAdbPath);
            devices.AddRange(custom.Select(Copy));
        }
        var listeners = ReadTcpListeners();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            using var config = JsonDocument.Parse(device.Config);
            if (!config.RootElement.TryGetProperty("extras", out var extras) || !extras.TryGetProperty("ld", out var ld)
                || !ld.TryGetProperty("pid", out var process)) continue;
            var serial = ResolveLdAddress(device.AdbSerial, process.GetInt32(), listeners);
            if (serial == device.AdbSerial) continue;
            AdbGameScreens.Run(device.AdbPath, ["connect", serial]);
            devices[i] = new(device.Name, device.AdbPath, serial, device.ScreencapMethods,
                device.InputMethods, device.Config, device.AgentPath);
        }
        return devices.DistinctBy(d => d.AdbSerial, StringComparer.Ordinal).ToList();
    }

    private static AdbDeviceInfo Copy(AdbDeviceInfo device)
        => new(device.Name, device.AdbPath, device.AdbSerial, device.ScreencapMethods, device.InputMethods, device.Config, device.AgentPath);

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    // Resolve the manager-provided instance index afresh, instead of guessing from a port or
    // retaining metadata after MuMu is reconfigured. A serial alone is not unique across installs.
    internal static MuMuCaptureConfig? FindMuMuCaptureConfig(string adbPath, string serial, out string reason)
    {
        try
        {
            using var found = MaaToolkit.Shared.AdbDevice.Find();
            var matches = found.Where(d => d.AdbSerial == serial && SamePath(d.AdbPath, adbPath)).ToList();
            if (matches.Count != 1)
            {
                reason = matches.Count == 0 ? "未发现对应的 MuMu 实例信息" : "无法唯一确认 MuMu 实例";
                return null;
            }

            using var config = JsonDocument.Parse(matches[0].Config);
            if (!config.RootElement.TryGetProperty("extras", out var extras) || extras.ValueKind != JsonValueKind.Object
                || !extras.TryGetProperty("mumu", out var mumu) || mumu.ValueKind != JsonValueKind.Object
                || !mumu.TryGetProperty("enable", out var enabled) || enabled.ValueKind != JsonValueKind.True
                || !mumu.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String
                || !mumu.TryGetProperty("index", out var indexElement) || indexElement.ValueKind != JsonValueKind.Number
                || !indexElement.TryGetInt32(out var index) || index < 0)
            {
                reason = "设备发现未提供完整的 MuMu 实例信息";
                return null;
            }

            var path = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            {
                reason = "MuMu 安装路径不可用";
                return null;
            }
            string? library = null;
            if (mumu.TryGetProperty("lib", out var libElement))
            {
                if (libElement.ValueKind != JsonValueKind.String)
                {
                    reason = "MuMu 截图库路径无效";
                    return null;
                }
                library = libElement.GetString();
                if (!string.IsNullOrEmpty(library) && (!Path.IsPathFullyQualified(library) || !File.Exists(library)))
                {
                    reason = "MuMu 截图库路径不可用";
                    return null;
                }
            }
            if (string.IsNullOrEmpty(library)
                && !File.Exists(Path.Combine(path, "nx_main", "sdk", "external_renderer_ipc.dll"))
                && !File.Exists(Path.Combine(path, "shell", "sdk", "external_renderer_ipc.dll")))
            {
                reason = "未找到 MuMu 截图库";
                return null;
            }
            reason = "";
            return new MuMuCaptureConfig(path, index, library);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AdbCapture", $"查找 MuMu 实例信息失败：{ex.Message}");
            reason = "无法读取 MuMu 实例信息";
            return null;
        }
    }

    public static EmulatorKind Kind(string name, string adbPath)
        => name.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase) || adbPath.Contains("leidian", StringComparison.OrdinalIgnoreCase)
            || adbPath.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase) ? EmulatorKind.LDPlayer
            : name.Contains("MuMu", StringComparison.OrdinalIgnoreCase) || adbPath.Contains("MuMu", StringComparison.OrdinalIgnoreCase)
                ? EmulatorKind.MuMu : EmulatorKind.Unknown;

    public static string ResolveLdAddress(string serial, int processId, IReadOnlyList<TcpListenerOwner> listeners)
    {
        if (!serial.StartsWith("emulator-", StringComparison.Ordinal) || !int.TryParse(serial[9..], out var consolePort)) return serial;
        var port = consolePort + 1;
        // MuMu's specific loopback binding wins over LDPlayer's wildcard binding on the same port.
        if (listeners.Any(l => l.Port == port && l.Address == "0.0.0.0" && l.ProcessId == processId)
            && listeners.Any(l => l.Port == port && l.Address == "127.0.0.1" && l.ProcessId != processId)
            && !listeners.Any(l => l.Port == port && l.Address == "127.0.0.2"))
            return $"127.0.0.2:{port}";
        return serial;
    }

    private static IReadOnlyList<TcpListenerOwner> ReadTcpListeners()
    {
        var size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        while (error == 122)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
                if (error == 122) continue; // Listener rows may change between the size query and the copy.
                if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
                var result = new List<TcpListenerOwner>();
                for (var i = 0; i < Marshal.ReadInt32(buffer); i++)
                {
                    var row = buffer + 4 + i * 24;
                    var address = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4))).ToString();
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                    result.Add(new(address, port, Marshal.ReadInt32(row, 20)));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        return [];
    }
}
