using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class Issue32ViewTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetWindowText(IntPtr window, string text);

    public static void VerifyWindowAssociation()
    {
        using var first = new HwndSource(new HwndSourceParameters("MuMu A") { WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 1600, Height = 900 });
        using var firstChild = new HwndSource(new HwndSourceParameters("nemudisplay") { ParentWindow = first.Handle, WindowStyle = 0x40000000, Width = 1600, Height = 900 });
        using var second = new HwndSource(new HwndSourceParameters("MuMu B") { WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 1600, Height = 900 });
        using var secondChild = new HwndSource(new HwndSourceParameters("nemudisplay") { ParentWindow = second.Handle, WindowStyle = 0x40000000, Width = 1600, Height = 900 });
        using var third = new HwndSource(new HwndSourceParameters("MuMu C") { WindowStyle = unchecked((int)0x80000000), PositionX = -20000, PositionY = -20000, Width = 1600, Height = 900 });
        using var thirdChild = new HwndSource(new HwndSourceParameters("nemudisplay") { ParentWindow = third.Handle, WindowStyle = 0x40000000, Width = 1600, Height = 900 });
        using var other = new HwndSource(new HwndSourceParameters("Other app"));
        GameConnectionService.GetWindowKind(first.Handle).Should().Be(EmulatorKind.MuMu);
        var settings = new GameConnectionSettings { Source = GameCaptureSource.Adb, WindowTitle = "MuMu A", AdbSerial = "isolated-test" };
        var display = new AdbGameDisplay(0, "100", "com.hypergryph.arknights", true);
        var calls = 0;
        using var gate = new ManualResetEventSlim(true);
        using var connection = new GameConnectionService(settings, first.Handle, _ =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            return [display];
        });
        connection.RefreshDisplay(); connection.PendingDisplayRead!.GetAwaiter().GetResult(); connection.RefreshDisplay().Should().BeTrue();
        var original = connection.CurrentDisplay;
        var revision = connection.CaptureRevision;
        gate.Reset();
        var watch = Stopwatch.StartNew();
        connection.FindGameWindow(second.Handle).Should().Be(second.Handle, "the foreground poll must not wait for ADB");
        var associationMs = watch.Elapsed.TotalMilliseconds;
        connection.CaptureRevision.Should().NotBe(revision, "old screenshots and OCR are invalidated immediately");
        connection.RefreshDisplay().Should().BeFalse();
        var pending = connection.PendingDisplayRead!;
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, 5000).Should().BeTrue();
        watch.Restart();
        for (int i = 0; i < 1000; i++) connection.FindGameWindow(second.Handle).Should().Be(second.Handle);
        var pollingMs = watch.Elapsed.TotalMilliseconds;
        calls.Should().Be(2, "stable desktop polling never issues ADB commands");
        connection.FindGameWindow(third.Handle).Should().Be(third.Handle, "another switch works while the prior ADB query is blocked");
        display = new(2, "200", "com.hypergryph.arknights.bilibili", true);
        gate.Set(); pending.GetAwaiter().GetResult();
        connection.RefreshDisplay().Should().BeFalse("a late screen response belongs to the superseded window");
        connection.CurrentDisplay.Should().Be(original);
        display = new(3, "300", "com.hypergryph.arknights", true);
        SetWindowText(third.Handle, "MuMu C · 游戏标签更新").Should().BeTrue();
        connection.RefreshDisplay(); connection.PendingDisplayRead!.GetAwaiter().GetResult(); connection.RefreshDisplay().Should().BeTrue();
        connection.CurrentDisplay.Should().Be(display);
        connection.FindGameWindow(third.Handle).Should().Be(third.Handle, "confirmed Android focus refreshes a changed desktop caption");
        connection.Settings.WindowTitle.Should().Be("MuMu C · 游戏标签更新");
        connection.FindGameWindow(other.Handle).Should().Be(third.Handle);
        connection.FindGameWindow(IntPtr.Zero).Should().Be(third.Handle);
        var stableRevision = connection.CaptureRevision;
        for (int i = 0; i < 1000; i++) connection.FindGameWindow(third.Handle);
        connection.CaptureRevision.Should().Be(stableRevision);

        connection.FindGameWindow(second.Handle).Should().Be(second.Handle);
        connection.RefreshDisplay(); connection.PendingDisplayRead!.GetAwaiter().GetResult();
        connection.RefreshDisplay().Should().BeFalse("the former Android screen must not be accepted for a new desktop window");
        Thread.Sleep(1050);
        display = new(2, "200", "com.hypergryph.arknights.bilibili", true);
        connection.RefreshDisplay(); connection.PendingDisplayRead!.GetAwaiter().GetResult(); connection.RefreshDisplay().Should().BeTrue();
        connection.CurrentDisplay.Should().Be(display);
        using var manual = new GameConnectionService(settings with { FollowFocusedGame = false }, first.Handle);
        manual.FindGameWindow(second.Handle).Should().Be(first.Handle);
        manual.Settings.WindowTitle.Should().Be("MuMu A");
        File.WriteAllText(Path.Combine(TestOutput.Directory, "issue32-window-performance.json"), JsonSerializer.Serialize(new
        { AssociationMs = associationMs, StablePolls = 1000, PollingMs = pollingMs, QueriesDuringBlockedPolls = 1, LateResponseRejected = true, OldDisplayRejected = true }));
    }
}
