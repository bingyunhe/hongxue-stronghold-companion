using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Views;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class StageRepairViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var evidence = new Dictionary<string, double>();
        foreach (bool missing in new[] { false, true })
        {
            var operators = Enumerable.Range(1, 9).Select(i => new Operator { Name = $"炎{i}", Tier = 2, CoreCovenants = ["炎"] }).ToList();
            operators.Add(new() { Name = "维1", Tier = 2, CoreCovenants = ["维"] });
            operators.Add(new() { Name = "低阶禁用", Tier = 1, CoreCovenants = ["炎"] });
            CovenantInfo[] covenants = [new() { Name = "炎", BondType = "SEASON", ActivateCount = 3 }, new() { Name = "维", BondType = "SEASON", ActivateCount = 3 }];
            var source = new Lineup { Id = "preview-source", Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })],
                TargetCovenants = [new() { Name = "炎", Tier = 2 }], SklandGuide = new() { Stages =
                [new() { Id = "opening", Name = "开局 · 自定义阶段名称", StartRound = 1, EndRound = 3, Slots = [new() { OperatorName = "炎6" }, new() { OperatorName = "维1" }] },
                 new() { Id = "middle", Name = "前期", StartRound = 4, EndRound = 6, Slots = [new() { OperatorName = missing ? "低阶禁用" : "维1" }] },
                 new() { Id = "final", Name = "成型阵容", IsFinal = true, Slots = [.. Enumerable.Range(1, 6).Select(i => new LineupSlot { OperatorName = $"炎{i}" })] }] } };
            var original = JsonSerializer.Serialize(source);
            var bans = new BanViewModel(); bans.SetBans(["炎6", "低阶禁用"]);
            var vm = new LineupPlannerViewModel(new(operators, covenants), operators, bans, EquipmentTestData.Rules) { DebounceMilliseconds = 0 };
            vm.BeginRepair(source, source.TargetCovenants, ["炎6"]); vm.IsActive = true; Complete(vm.PendingSearch!);
            var item = vm.VisibleLineups[0];
            ThemeManager.Apply(theme, night);
            var view = new LineupPlannerView { DataContext = vm };
            var surface = new Grid(); surface.SetResourceReference(Panel.BackgroundProperty, "PanelBackgroundBrush"); surface.Children.Add(view);
            var host = new Window { Content = surface, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 400, Height = 900 };
            try
            {
                host.Show(); Pump();
                vm.ShowGoals.Should().BeTrue();
                var navigate = (Button)view.FindName("PlannerNavigationButton");
                navigate.Content.Should().Be($"查看 {vm.TotalResults} 套 →");
                navigate.Command.Execute(null); Pump();
                vm.ShowResultList.Should().BeTrue();
                var preview = Desc<Button>(view).Single(b => ReferenceEquals(b.DataContext, item) && b.Content as string == "预览各阶段修补");
                var save = Desc<Button>(view).Single(b => ReferenceEquals(b.DataContext, item) && b.Content as string == "覆盖原阵容");
                save.IsEnabled.Should().BeFalse();
                preview.Command.Execute(preview.CommandParameter); Complete(vm.PreviewStageRepairCommand.ExecutionTask!); Pump();
                save.IsEnabled.Should().Be(!missing);
                foreach (int width in new[] { 280, 400 })
                {
                    host.Width = width; host.UpdateLayout(); Pump();
                    var heading = Desc<TextBlock>(view).First(t => t.DataContext is StageRepairChange && t.Text == "开局 · 自定义阶段名称");
                    heading.BringIntoView(); Pump();
                    Save(surface, $"{theme}-{night}-{width}-{(missing ? "missing" : "ready")}");
                    foreach (var text in Desc<TextBlock>(view).Where(t => t.IsVisible && t.DataContext is StageRepairChange && t.Text.Length > 0))
                    {
                        var border = ParentStage(text);
                        var foreground = ((SolidColorBrush)text.Foreground).Color;
                        var background = ((SolidColorBrush)border.Background).Color;
                        var ratio = Contrast(foreground, background);
                        evidence[$"{width}-{missing}-{text.Text}"] = ratio;
                        ratio.Should().BeGreaterThanOrEqualTo(4.5, "stage preview text is readable over the actual stage background");
                        text.ActualWidth.Should().BeLessThanOrEqualTo(border.ActualWidth);
                    }
                    save.BringIntoView(); Pump(); Save(surface, $"{theme}-{night}-{width}-{(missing ? "missing" : "ready")}-actions");
                    var bounds = save.TransformToAncestor(view).TransformBounds(new Rect(save.RenderSize));
                    bounds.Left.Should().BeGreaterThanOrEqualTo(0); bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth + 1);
                }
                if (missing) item.StageRepair!.Stages.Should().Contain(s => s.Missing == 1 && s.WarningText.Contains("缺 1 人"));
                JsonSerializer.Serialize(source).Should().Be(original);
            }
            finally { host.Close(); view.DataContext = null; vm.IsActive = false; }
        }
        File.WriteAllText(Path.Combine(TestOutput.Directory, $"stage-repair-{theme}-{night}-contrast.json"), JsonSerializer.Serialize(evidence));
    }
    private static Border ParentStage(DependencyObject text)
    {
        for (var parent = VisualTreeHelper.GetParent(text); ; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Border { DataContext: StageRepairChange } border) return border;
    }
    private static double Contrast(Color first, Color second)
    {
        static double L(Color c)
        {
            static double V(byte b) { var v = b / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * V(c.R) + 0.7152 * V(c.G) + 0.0722 * V(c.B);
        }
        var a = L(first); var b = L(second); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Complete(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Pump(); Thread.Sleep(2); }
        task.IsCompleted.Should().BeTrue(); task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Desc<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    private static void Save(FrameworkElement surface, string name)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(TestOutput.Directory, "stage-repair"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
}
