using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Tests.Views;

public partial class ThemeAndHighlightTests
{
    [Fact, Trait("Category", "LocalScreenshot")]
    public Task ManualFieldButton_ReplaysFirstRepeatAndMovedBoard() => ui.RunAsync(() =>
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var maps = GarrisonBoardStore.LoadAsync(data).GetAwaiter().GetResult()!;
        var operators = new JsonDataStore(data).LoadOperatorsAsync().GetAwaiter().GetResult().ToDictionary(op => op.Name);
        using var ocr = new MaaOcrEngine(data);
        using var connection = new GameConnectionService(new() { Source = GameCaptureSource.Adb });
        byte[] frame = [];
        int captures = 0;
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(ocr, []), ocr, new(), [], getConnection: () => connection)
        {
            EconomyReader = new(ocr), BoardRecognizer = new(maps), LiveReader = new(data, maps),
            FieldTiers = operators.ToDictionary(p => p.Key, p => p.Value.Tier)
        };
        var view = new DeployView();
        view.Initialize(operators, [], maps, new TrackingViewModel(),
            new SettingsManager(TestOutput.Directory), new DeployPoolState(), false);
        view.ConnectLiveField(scanner);
        var window = new Window { Content = view, Width = 400, Height = 550, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        var context = SynchronizationContext.Current;
        var rows = new List<object>();
        int reused = 0, pendingRecomputed = 0;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            window.Show();
            var button = (Button)view.FindName("ReadFieldButton");
            foreach (int round in new[] { 1, 2, 4, 9 })
            {
                frame = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Board", $"redfloor-memory-r{round}.png"));
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    var count = captures;
                    var previous = scanner.LiveRoster;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    var clock = Stopwatch.StartNew();
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (!button.IsEnabled && clock.ElapsedMilliseconds < 10000)
                    {
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        Thread.Sleep(1);
                    }
                    Assert.True(button.IsEnabled);
                    view.UpdateLayout();
                    new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32).Render(view);
                    var elapsed = clock.Elapsed.TotalMilliseconds;
                    Assert.Equal(count + 1, captures);
                    Assert.Equal(round, scanner.LiveRoster.Round);
                    Assert.NotEmpty(scanner.LiveRoster.Units);
                    var units = scanner.LiveRoster.Units.Select(u => new { slot = u.Mark.Slot.ToString(), u.Name }).ToArray();
                    if (repeat > 0)
                    {
                        foreach (var unit in scanner.LiveRoster.Units)
                        {
                            var old = previous.Units.Single(u => u.Mark.Slot == unit.Mark.Slot);
                            bool same = unit.Mark.ShapeCandidates.Length > 0 && ReferenceEquals(unit.Mark.ShapeCandidates, old.Mark.ShapeCandidates)
                                || unit.Mark.Scores != null && ReferenceEquals(unit.Mark.Scores, old.Mark.Scores);
                            if (old.Name == null)
                            {
                                Assert.False(same);
                                pendingRecomputed++;
                            }
                            if (same) reused++;
                        }
                    }
                    rows.Add(new { round, repeat, elapsed, units });
                }
                if (round is 1 or 4)
                {
                    foreach (var (slot, name) in round == 1 ? new[] { ("C7", "摆渡人") } : new[] { ("C5", "星极"), ("C3", "雪猎") })
                        Assert.True(scanner.SetLiveIdentity(scanner.LiveRoster.Units.Single(u => u.Mark.Slot.ToString() == slot), name));
                }
            }
            File.WriteAllText(Path.Combine(TestOutput.Directory, "manual-field-replay.json"), JsonSerializer.Serialize(rows));
            Assert.True(reused > 0 && pendingRecomputed > 0, $"reused={reused}, pending={pendingRecomputed}");
            Assert.Equal("摆渡人", scanner.LiveRoster.Units.Single(u => u.Mark.Slot.ToString() == "B7").Name);
            Assert.Equal("星极", scanner.LiveRoster.Units.Single(u => u.Mark.Slot.ToString() == "C6").Name);
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(context); }
    }, nameof(ManualFieldButton_ReplaysFirstRepeatAndMovedBoard));
}
