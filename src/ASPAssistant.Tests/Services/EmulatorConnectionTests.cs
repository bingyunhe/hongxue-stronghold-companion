using ASPAssistant.Core.Data;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;
using System.Text.Json;

namespace ASPAssistant.Tests.Services;

public class EmulatorConnectionTests
{
    [Fact]
    public async Task ManualDisplayTestAwaitsOnePendingQuery()
    {
        var display = new AdbGameDisplay(0, "77", "com.hypergryph.arknights", true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = 0;
        using var connection = new GameConnectionService(new() { Source = GameCaptureSource.Adb }, IntPtr.Zero, _ =>
        {
            Interlocked.Increment(ref queries);
            started.SetResult();
            finish.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return [display];
        });

        connection.RefreshDisplay().Should().BeFalse();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = Task.Run(() =>
        {
            captureStarted.SetResult();
            return connection.RefreshDisplay(waitForInitialDisplay: true);
        });
        try
        {
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await Task.WhenAny(test, Task.Delay(100))).Should().NotBeSameAs(test);
        }
        finally { finish.SetResult(); }

        (await test.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        connection.CurrentDisplay.Should().Be(display);
        queries.Should().Be(1);
    }

    [Theory]
    [InlineData(true, "ADB 查询超时")]
    [InlineData(false, "未找到所选明日方舟屏幕")]
    public async Task ManualCaptureReportsTheActualQueryError(bool timeout, string message)
    {
        var calls = 0;
        using var connection = new GameConnectionService(new()
        {
            Source = GameCaptureSource.Adb, AdbPath = Environment.ProcessPath!, AdbSerial = "test",
            AndroidDisplayId = 0, AndroidPackageName = "com.hypergryph.arknights", FollowFocusedGame = false,
        }, IntPtr.Zero, _ =>
        {
            Interlocked.Increment(ref calls);
            if (timeout) throw new IOException(message);
            return [new(2, "99", "com.hypergryph.arknights.bilibili", true)];
        });

        var png = await Task.Run(() => connection.CaptureAdbScreen(1920, waitForInitialDisplay: true));
        png.Should().BeNull();
        connection.Status.Should().StartWith(message);
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData("127.0.0.1:16384", false)]
    [InlineData("localhost:16384", false)]
    [InlineData("[::1]:16384", false)]
    [InlineData("127.0.0.2:5555", true)]
    public void ScreenQueryReconnectsMissingTransportButDoesNotAddHealthyQueries(string serial, bool offline)
    {
        var settings = new GameConnectionSettings { AdbPath = "selected-adb.exe", AdbSerial = serial };
        var connected = false;
        var commands = new List<string[]>();
        string Run(string path, IEnumerable<string> arguments)
        {
            path.Should().Be(settings.AdbPath);
            var args = arguments.ToArray();
            commands.Add(args);
            if (args[0] == "connect")
            {
                args.Should().Equal("connect", serial);
                connected = true;
                return $"connected to {serial}\n";
            }
            args.Take(3).Should().Equal("-s", serial, "shell");
            if (!connected) throw new IOException(offline ? "ADB 查询失败：adb.exe: device offline"
                : $"ADB 查询失败：adb.exe: device '{serial}' not found");
            return args[^1] switch
            {
                "activities" => "Display #0\n topResumedActivity=ActivityRecord{abc u0 com.hypergryph.arknights/.MainActivity t1}",
                "display" => "Display 0:\n uniqueId=local:77",
                "input" => "FocusedDisplayId: 0",
                _ => throw new InvalidOperationException("Unexpected ADB command")
            };
        }

        var screens = AdbGameScreens.Find(settings, Run);
        screens.Should().ContainSingle().Which.PhysicalDisplayId.Should().Be("77");
        commands.Should().HaveCount(5);
        commands[1].Should().Equal("connect", serial);

        commands.Clear();
        AdbGameScreens.Find(settings, Run).Should().Equal(screens);
        commands.Should().HaveCount(3).And.OnlyContain(args => args[0] == "-s");

        connected = false; // The existing ADB server lost its device registration again.
        commands.Clear();
        AdbGameScreens.Find(settings, Run).Should().Equal(screens);
        commands.Should().HaveCount(5);
        commands.Count(args => args[0] == "connect").Should().Be(1);
    }

    [Theory]
    [InlineData("127.0.0.1:16384", "ADB 查询超时")]
    [InlineData("127.0.0.1:16384", "ADB 查询失败：device unauthorized")]
    [InlineData("127.0.0.1:16384", "ADB 查询失败：permission denied")]
    [InlineData("emulator-5554", "ADB 查询失败：device 'emulator-5554' not found")]
    [InlineData("usb-device", "ADB 查询失败：device 'usb-device' not found")]
    public void OtherFailuresAndNonNetworkSerialsDoNotTriggerConnect(string serial, string message)
    {
        var calls = 0;
        var failure = new IOException(message);
        Action find = () => AdbGameScreens.Find(new() { AdbSerial = serial }, (_, _) => { calls++; throw failure; });
        find.Should().Throw<IOException>().Which.Should().BeSameAs(failure);
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedConnectionOrFailedRetryStopsWithoutLooping(bool retryFails)
    {
        const string serial = "127.0.0.1:16384";
        var calls = 0;
        Action find = () => AdbGameScreens.Find(new() { AdbSerial = serial }, (_, arguments) =>
        {
            calls++;
            if (arguments.First() == "connect")
                return retryFails ? $"already connected to {serial}" : $"failed to connect to '{serial}': Connection refused";
            throw new IOException($"ADB 查询失败：adb.exe: device '{serial}' not found");
        });
        find.Should().Throw<IOException>();
        calls.Should().Be(retryFails ? 3 : 2);
    }

    [Theory]
    [InlineData("topResumedActivity=", true)]
    [InlineData("mResumedActivity:", false)]
    public void FindsOfficialAndBServerOnTheirOwnScreens(string prefix, bool focus)
    {
        var result = AdbGameScreens.Parse($$"""
            Display #0 (activities):
              {{prefix}} ActivityRecord{abc u0 com.hypergryph.arknights/.MainActivity t1}
            Display #2 (activities):
              {{prefix}} ActivityRecord{def u0 com.hypergryph.arknights.bilibili/.MainActivity t2}
            Display #3 (activities):
              {{prefix}} ActivityRecord{def u0 com.other.game/.MainActivity t3}
            """, """
            Display 0:
              mDisplayInfo=DisplayInfo{uniqueId "local:4619827820427265280"}
            Display 2:
              mDisplayInfo=DisplayInfo{uniqueId='local:77'}
            """, focus ? "FocusedDisplayId: 2" : "");
        result.Should().HaveCount(2);
        result[0].PhysicalDisplayId.Should().Be("4619827820427265280");
        result[1].PhysicalDisplayId.Should().Be("77");
        result[1].IsFocused.Should().Be(focus);
    }

    [Fact]
    public void FocusCanFollowBServerWhileManualModeKeepsOfficialSelection()
    {
        AdbGameDisplay[] displays = [new(0, "100", "com.hypergryph.arknights", false), new(2, "200", "com.hypergryph.arknights.bilibili", true)];
        var saved = new GameConnectionSettings { AndroidPackageName = displays[0].PackageName, AndroidDisplayId = 0 };
        AdbGameScreens.Select(displays, saved).Should().Be(displays[1]);
        AdbGameScreens.Select(displays, saved with { FollowFocusedGame = false }).Should().Be(displays[0]);
        AdbGameScreens.Select([displays[0] with { DisplayId = 4 }], saved with { FollowFocusedGame = false }).DisplayId.Should().Be(4);
    }

    [Fact]
    public void AmbiguousOrMissingScreensCannotFallBackToDefaultScreen()
    {
        AdbGameDisplay[] displays = [new(0, "100", "com.hypergryph.arknights", false), new(2, "200", "com.hypergryph.arknights", false)];
        var select = () => AdbGameScreens.Select(displays, new());
        select.Should().Throw<IOException>();
        select = () => AdbGameScreens.Select([], new());
        select.Should().Throw<IOException>();
        var capture = () => AdbGameScreens.ControllerConfig(new(2, null, displays[0].PackageName, false));
        capture.Should().Throw<IOException>();
        AdbGameScreens.ControllerConfig(new(0, null, displays[0].PackageName, false)).Should().Contain("screencap -p");
    }

    [Fact]
    public void ScreenshotUsesPhysicalIdAndInputUsesLogicalId()
    {
        using var config = JsonDocument.Parse(AdbGameScreens.ControllerConfig(new(2, "4619827820427265280", "com.hypergryph.arknights", true)));
        config.RootElement.GetProperty("command").GetProperty("ScreencapEncode")[4].GetString().Should().Be("screencap -p -d 4619827820427265280");
        config.RootElement.GetProperty("command").GetProperty("Swipe")[4].GetString().Should().StartWith("input -d 2 swipe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("4619827820427265280")]
    public void DefaultScreenSwipeUsesAndroid9SyntaxWithoutDisplayOption(string? physicalDisplayId)
    {
        using var config = JsonDocument.Parse(AdbGameScreens.ControllerConfig(new(0, physicalDisplayId, "com.hypergryph.arknights", true)));
        config.RootElement.GetProperty("command").GetProperty("Swipe").EnumerateArray().Select(part => part.GetString())
            .Should().Equal("{ADB}", "-s", "{ADB_SERIAL}", "shell", "input swipe {X1} {Y1} {X2} {Y2} {DURATION}");
        config.RootElement.GetProperty("command").GetProperty("ScreencapEncode")[4].GetString()
            .Should().Be(physicalDisplayId is null ? "screencap -p" : $"screencap -p -d {physicalDisplayId}");
    }

    [Theory]
    [InlineData(true, true, false, "127.0.0.2:5555")]
    [InlineData(false, true, false, "emulator-5554")]
    [InlineData(true, false, false, "emulator-5554")]
    [InlineData(true, true, true, "emulator-5554")]
    public void LdPortCollisionOnlyUsesUnoccupiedLoopbackWhenOwnersConfirmIt(bool wildcard, bool conflict, bool occupied, string expected)
    {
        var listeners = new List<TcpListenerOwner>();
        if (wildcard) listeners.Add(new("0.0.0.0", 5555, 10));
        if (conflict) listeners.Add(new("127.0.0.1", 5555, 20));
        if (occupied) listeners.Add(new("127.0.0.2", 5555, 30));
        EmulatorDiscovery.ResolveLdAddress("emulator-5554", 10, listeners).Should().Be(expected);
        EmulatorDiscovery.ResolveLdAddress("127.0.0.1:16384", 10, listeners).Should().Be("127.0.0.1:16384");
    }

    [Fact]
    public async Task DisplaySelectionRoundTripsAndPreviousConfigurationIsBackedUp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "screen-settings-" + Guid.NewGuid().ToString("N"));
        var manager = new SettingsManager(folder);
        var saved = new GameConnectionSettings { Source = GameCaptureSource.Adb, Emulator = EmulatorKind.LDPlayer,
            AndroidPackageName = "com.hypergryph.arknights.bilibili", AndroidDisplayId = 2, FollowFocusedGame = false };
        try
        {
            await manager.SaveGameConnectionAsync(new());
            var previous = File.ReadAllText(Path.Combine(folder, "game-connection.json"));
            await manager.SaveGameConnectionAsync(saved);
            (await manager.LoadGameConnectionAsync()).Should().Be(saved);
            File.ReadAllText(Path.Combine(folder, "game-connection.json.bak")).Should().Be(previous);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SnappingUsesDipsAndPreservesSizeOnNegativeCoordinateMonitors(double scale)
    {
        var work = new RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        var near = new RECT { Left = -1920 + (int)(11 * scale), Top = 200, Right = -1520 + (int)(11 * scale), Bottom = 800 };
        var snapped = WindowGeometry.SnapBounds(near, work, scale, null);
        snapped.Left.Should().Be(-1920);
        snapped.Width.Should().Be(near.Width);
        snapped.Height.Should().Be(near.Height);
        near.Left += (int)(3 * scale); near.Right += (int)(3 * scale);
        WindowGeometry.SnapBounds(near, work, scale, null).Left.Should().Be(near.Left);
    }

    [Fact]
    public void SnappingOnlyUsesGameEdgesWhenTheOtherAxisOverlaps()
    {
        var work = new RECT { Right = 2000, Bottom = 1400 };
        var game = new RECT { Left = 200, Top = 200, Right = 1000, Bottom = 800 };
        var bounds = new RECT { Left = 1007, Top = 300, Right = 1307, Bottom = 900 };
        WindowGeometry.SnapBounds(bounds, work, 1, game).Left.Should().Be(1000);
        bounds.Top = 850; bounds.Bottom = 1300;
        WindowGeometry.SnapBounds(bounds, work, 1, game).Left.Should().Be(1007);
    }
}
