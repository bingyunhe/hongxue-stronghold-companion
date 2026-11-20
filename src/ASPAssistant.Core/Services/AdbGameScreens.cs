using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public static class AdbGameScreens
{
    public static string Run(string adbPath, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(adbPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = new System.ComponentModel.Win32Exception(ex.NativeErrorCode).Message;
            throw new IOException($"ADB 程序启动失败（Windows 错误 {ex.NativeErrorCode}）：{reason}。请点「浏览…」重新选择 adb.exe。", ex);
        }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("ADB 查询超时，请检查模拟器连接");
        }
        if (process.ExitCode != 0) throw new IOException($"ADB 查询失败：{error.GetAwaiter().GetResult().Trim()}");
        return output.GetAwaiter().GetResult();
    }

    public static IReadOnlyList<AdbGameDisplay> Find(GameConnectionSettings settings)
        => Find(settings, Run);

    internal static IReadOnlyList<AdbGameDisplay> Find(GameConnectionSettings settings,
        Func<string, IEnumerable<string>, string> run)
    {
        string activities;
        try
        {
            activities = run(settings.AdbPath, ["-s", settings.AdbSerial, "shell", "dumpsys", "activity", "activities"]);
        }
        catch (IOException ex) when (
            (ex.Message.Contains($"device '{settings.AdbSerial}' not found", StringComparison.Ordinal)
                || ex.Message.Contains("device offline", StringComparison.Ordinal))
            && Uri.TryCreate($"tcp://{settings.AdbSerial}", UriKind.Absolute, out var endpoint)
            && endpoint.Host.Length > 0 && endpoint.Port > 0 && endpoint.UserInfo.Length == 0
            && endpoint.PathAndQuery is "" or "/")
        {
            var result = run(settings.AdbPath, ["connect", settings.AdbSerial]).Trim();
            // adb connect can report connection refusal on stdout with exit code zero.
            if (result != $"connected to {settings.AdbSerial}" && result != $"already connected to {settings.AdbSerial}")
                throw new IOException($"ADB 连接失败：{result}");
            activities = run(settings.AdbPath, ["-s", settings.AdbSerial, "shell", "dumpsys", "activity", "activities"]);
        }
        return Parse(activities,
            run(settings.AdbPath, ["-s", settings.AdbSerial, "shell", "dumpsys", "display"]),
            run(settings.AdbPath, ["-s", settings.AdbSerial, "shell", "dumpsys", "input"]));
    }

    public static IReadOnlyList<AdbGameDisplay> Parse(string activities, string displays, string input)
    {
        var physical = new Dictionary<int, string>();
        var blocks = Regex.Matches(displays, @"(?m)^\s*Display (\d+):\s*\r?$");
        for (var i = 0; i < blocks.Count; i++)
        {
            var end = i + 1 < blocks.Count ? blocks[i + 1].Index : displays.Length;
            var id = Regex.Match(displays[blocks[i].Index..end], "uniqueId[=\\s]+[\"']?local:(\\d+)");
            if (id.Success) physical[int.Parse(blocks[i].Groups[1].Value)] = id.Groups[1].Value;
        }
        var focused = Regex.Match(input, @"(?:m?FocusedDisplayId)\s*[:=]\s*(\d+)");
        int? focusedId = focused.Success ? int.Parse(focused.Groups[1].Value) : null;
        var result = new List<AdbGameDisplay>();
        var displayId = 0;
        foreach (var line in activities.Split('\n'))
        {
            var display = Regex.Match(line, @"^\s*Display #(\d+)\b");
            if (display.Success) displayId = int.Parse(display.Groups[1].Value);
            var activity = Regex.Match(line, @"(?:topResumedActivity|mResumedActivity)\s*[:=].*?\bu\d+\s+([\w.]+)/");
            if (!activity.Success) continue;
            var package = activity.Groups[1].Value;
            if (package is not ("com.hypergryph.arknights" or "com.hypergryph.arknights.bilibili")) continue;
            physical.TryGetValue(displayId, out var physicalId);
            var item = new AdbGameDisplay(displayId, physicalId, package, displayId == focusedId);
            if (!result.Contains(item)) result.Add(item);
        }
        if (focusedId is null)
        {
            var resumed = Regex.Match(activities, @"(?m)^\s*ResumedActivity:.*?\bu\d+\s+([\w.]+)/");
            var matches = result.Where(d => resumed.Success && d.PackageName == resumed.Groups[1].Value).ToList();
            if (matches.Count == 1) result = [.. result.Select(d => d with { IsFocused = d.DisplayId == matches[0].DisplayId })];
        }
        return result.OrderBy(d => d.DisplayId).ToList();
    }

    public static AdbGameDisplay Select(IReadOnlyList<AdbGameDisplay> displays, GameConnectionSettings settings)
    {
        if (settings.FollowFocusedGame)
        {
            var focused = displays.Where(d => d.IsFocused).ToList();
            if (focused.Count == 1) return focused[0];
        }
        var matches = displays.Where(d => settings.AndroidPackageName.Length == 0 || d.PackageName == settings.AndroidPackageName).ToList();
        if (matches.Count > 1 && settings.AndroidDisplayId is { } id) matches = matches.Where(d => d.DisplayId == id).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new IOException("未找到所选明日方舟屏幕，请打开游戏并刷新游戏屏幕。"),
            _ => throw new IOException("发现多个明日方舟屏幕，请选择官服或B服及对应屏幕。")
        };
    }

    public static string ControllerConfig(AdbGameDisplay display)
    {
        if (display.DisplayId != 0 && display.PhysicalDisplayId is null)
            throw new IOException("该游戏屏幕未提供独立截图编号，无法安全截图，请选择可截图的游戏屏幕。");
        var capture = display.PhysicalDisplayId is { } id ? $"screencap -p -d {id}" : "screencap -p";
        return JsonSerializer.Serialize(new { command = new Dictionary<string, string[]>
        {
            ["ScreencapEncode"] = ["{ADB}", "-s", "{ADB_SERIAL}", "exec-out", capture],
            ["Swipe"] = ["{ADB}", "-s", "{ADB_SERIAL}", "shell", $"input{(display.DisplayId == 0 ? "" : $" -d {display.DisplayId}")} swipe {{X1}} {{Y1}} {{X2}} {{Y2}} {{DURATION}}"]
        } });
    }
}
