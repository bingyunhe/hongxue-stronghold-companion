using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class EditorRedesignViewTests
{
    public static void Verify(GameShopTheme theme, bool night)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "TestData", "Regulation"));
        var operators = data.LoadOperatorsAsync().GetAwaiter().GetResult();
        var (equipment, _) = data.LoadEquipmentAsync().GetAwaiter().GetResult();
        var (covenants, map) = data.LoadCovenantsAsync().GetAwaiter().GetResult();
        var strategies = data.LoadStrategiesAsync().GetAwaiter().GetResult();
        var calculator = new CovenantCalculator(operators, covenants, map);
        SearchMatch.LoadFile(Path.Combine(AppContext.BaseDirectory, "data", "search_keys.json"));
        var gear = equipment.Where(e => e.Type != EquipmentType.JobChange && e.Name != CovenantCalculator.ShapeshifterEquipment)
            .OrderBy(e => e.Tier).Take(4).ToList();
        var people = operators.Where(o => !o.IsCustom && o.RequiredStrategyId.Length == 0).Take(4).ToList();
        foreach (var wide in new[] { false, true })
        {
            ThemeManager.Apply(theme, night);
            var prefix = $"{theme}-{night}-{(wide ? "wide" : "narrow")}";
            var savedStrategies = new List<LineupStrategySlot>
            {
                new() { StrategyId = strategies[0].Id, Tags = [StrategyTag.阵容核心, StrategyTag.优选] },
                new() { StrategyId = "shared-unknown", Tags = [StrategyTag.优选, StrategyTag.次选] },
                new() { StrategyId = strategies[1].Id, Tags = [StrategyTag.次选] },
            };
            var source = new Lineup
            {
                Name = "编辑器回归", SourceUrl = "https://www.skland.com/article?id=example",
                Slots = [new() { OperatorName = people[0].Name, IsElite = true, Equipments = [gear[0].Name],
                    CandidateEquipments = [new() { Name = gear[1].Name, IsPriority = true }], Tags = [LineupTag.阵容核心] },
                    new() { OperatorName = people[1].Name }],
                EarlySlots = [new() { OperatorName = people[2].Name }], MidSlots = [new() { OperatorName = people[3].Name }],
                SupportSlots = [new() { OperatorName = people[2].Name, Tags = [LineupTag.输出] }],
                Strategies = savedStrategies, TargetCovenants = [new() { Name = "乌萨斯", Tier = 2 }]
            };
            var vm = new LineupEditorViewModel(source, operators, equipment, calculator);
            var editor = new LineupEditorWindow(vm, strategies);
            Run(editor, wide, () =>
            {
                ((Button)editor.FindName("SourcePageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.UpdateLayout(); Pump();
                ((FrameworkElement)editor.FindName("SourcePanel")).IsVisible.Should().BeTrue();
                ((Button)editor.FindName("LineupPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.UpdateLayout(); Pump();
                ((TextBox)editor.FindName("LineupNameBox")).ActualWidth.Should().BeGreaterThan(100);
                ((FrameworkElement)((ItemsControl)editor.FindName("SlotsHost")).Items[0]).ActualWidth.Should().BeGreaterThanOrEqualTo(200);
                Descendants<CheckBox>(editor).Should().Contain(c => Equals(c.Content, "精锐") && c.IsChecked == true);
                Save(editor, prefix + "-editor");
                ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            editor.Result.Should().NotBeNull();
            editor.Result!.SourceUrl.Should().Be(source.SourceUrl);
            editor.Result.Slots[0].CandidateEquipments.Single().IsPriority.Should().BeTrue();
            editor.Result.Strategies[0].Tags.Should().Equal(source.Strategies[0].Tags);
            editor.Result.EarlySlots.Should().BeEquivalentTo(source.EarlySlots);
            editor.Result.MidSlots.Should().BeEquivalentTo(source.MidSlots);
            editor.Result.SupportSlots.Should().BeEquivalentTo(source.SupportSlots);

            var picker = new OperatorPickerWindow(operators, "", new HashSet<string>());
            Run(picker, wide, () =>
            {
                var search = (TextBox)picker.FindName("SearchBox");
                var chosen = operators.First(o => !o.IsCustom && o.RequiredStrategyId.Length == 0 && SearchMatch.InitialsOf(o.Name).Length > 0);
                search.Text = SearchMatch.InitialsOf(chosen.Name).ToUpperInvariant();
                Pump();
                var list = (ListBox)picker.FindName("OperatorsList");
                list.Items.Cast<Operator>().Should().Contain(o => o.Name == chosen.Name);
                Save(picker, prefix + "-operators");
                picker.Close();
            });
            picker.Result.Should().BeNull("closing a picker is cancellation");

            var single = new EquipmentPickerWindow(equipment, calculator.EquipmentRules, people[0].Name + " · 装备格 2", gear[0].Name, false);
            Run(single, wide, () =>
            {
                var list = (ListBox)single.FindName("ItemsList");
                var row = list.Items.Cast<EquipmentPickRow>().Single(r => r.Name == gear[2].Name);
                list.ScrollIntoView(row);
                Pump();
                MouseClick((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row));
                ((ListBox)single.FindName("CoreMode")).SelectedIndex = 1;
                Save(single, prefix + "-equipment");
                ((Button)single.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            single.Result.Should().Be(gear[2].Name);
            single.IsCoreEquipment.Should().BeTrue();

            var candidates = new EquipmentPickerWindow(equipment, calculator.EquipmentRules, people[0].Name,
                [new() { Name = gear[1].Name }], [gear[0].Name]);
            Run(candidates, wide, () =>
            {
                var list = (ListBox)candidates.FindName("ItemsList");
                list.Items.Cast<EquipmentPickRow>().Should().NotContain(r => r.Name == gear[0].Name);
                var row = list.Items.Cast<EquipmentPickRow>().Single(r => r.Name == gear[2].Name);
                list.ScrollIntoView(row);
                Pump();
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                MouseClick(container);
                Pump();
                Descendants<Button>(container).Single(b => Equals(b.ToolTip, "设为优先")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                row.IsPriority.Should().BeTrue();
                ((TextBox)candidates.FindName("SearchBox")).Text = gear[1].Name;
                Pump();
                ((TextBox)candidates.FindName("SearchBox")).Clear();
                Pump();
                Save(candidates, prefix + "-candidates");
                ((Button)candidates.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            candidates.Candidates.Should().HaveCount(2);
            candidates.Candidates!.Single(c => c.Name == gear[2].Name).IsPriority.Should().BeTrue();
            candidates.Candidates.Single(c => c.Name == gear[1].Name).IsPriority.Should().BeFalse();

            var strategy = new StrategyPickerWindow(strategies, savedStrategies);
            Run(strategy, wide, () =>
            {
                ((ItemsControl)strategy.FindName("ChosenList")).Items.Count.Should().Be(2, "multiple known strategies remain selected");
                Save(strategy, prefix + "-strategies");
                Descendants<Button>(strategy).Single(b => Equals(b.Content, "完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            strategy.Picked.Should().HaveCount(3);
            strategy.Picked.Select(s => s.StrategyId).Should().Equal(savedStrategies.Select(s => s.StrategyId));
            strategy.Picked[0].Tags.Should().Equal(StrategyTag.阵容核心, StrategyTag.优选);
            strategy.Picked.Single(s => s.StrategyId == "shared-unknown").Tags.Should().Equal(StrategyTag.优选, StrategyTag.次选);
            vm.SetStrategies(strategy.Picked);
            vm.ToLineup().Strategies.Should().BeEquivalentTo(savedStrategies);

            var crowded = new StrategyPickerWindow(strategies, []);
            Run(crowded, wide, () =>
            {
                var list = (ListBox)crowded.FindName("ItemsList");
                var rows = list.Items.Cast<StrategyPickRow>().Take(10).ToList();
                foreach (var row in rows)
                {
                    list.ScrollIntoView(row);
                    Pump();
                    MouseClick((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row));
                    Pump();
                }
                ((ItemsControl)crowded.FindName("ChosenList")).Items.Count.Should().Be(9);
                rows[9].IsChosen.Should().BeFalse("a tenth choice must not be silently discarded when saved");
                ((TextBlock)crowded.FindName("HintText")).Text.Should().Contain("9");
                Save(crowded, prefix + "-strategy-capacity");
                Descendants<Button>(crowded).Single(b => Equals(b.Content, "完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            crowded.Picked.Should().HaveCount(9);

            var target = new TargetPickerWindow(covenants, vm.ComputeLate(), vm.TargetCovenants.ToList(), vm.HasSecondTier);
            Run(target, wide, () =>
            {
                var icons = Descendants<CovenantIcon>(target).ToList();
                icons.Should().HaveCount(25);
                icons.Should().NotContain(icon => icon.Covenant == "绝技");
                foreach (var icon in icons)
                {
                    var glyph = Descendants<Border>(icon).Single();
                    glyph.OpacityMask.Should().BeOfType<ImageBrush>();
                    glyph.Visibility.Should().Be(Visibility.Visible);
                    var bitmap = new RenderTargetBitmap((int)icon.ActualWidth, (int)icon.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(icon);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var sample = Enumerable.Range(0, pixels.Length / 4).MaxBy(i => pixels[i * 4 + 3]) * 4;
                    var alpha = pixels[sample + 3];
                    alpha.Should().BeGreaterThan((byte)20, $"{icon.Covenant} must visibly render");
                    var color = ((SolidColorBrush)icon.Brush!).Color;
                    Math.Abs(pixels[sample] - color.B * alpha / 255).Should().BeLessThanOrEqualTo(3);
                    Math.Abs(pixels[sample + 1] - color.G * alpha / 255).Should().BeLessThanOrEqualTo(3);
                    Math.Abs(pixels[sample + 2] - color.R * alpha / 255).Should().BeLessThanOrEqualTo(3);
                }
                var cells = Descendants<Button>(target).Where(b => b.Tag is string).ToList();
                cells.Single(b => Equals(b.Tag, "乌萨斯")).IsEnabled.Should().BeFalse();
                var arcane = cells.Single(b => Equals(b.Tag, "奥术"));
                arcane.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                target.Picked.Should().Equal(("奥术", 1));
                arcane.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                target.Picked.Should().Equal(("奥术", 2));
                arcane.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                target.Picked.Should().BeEmpty();
                arcane.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Save(target, prefix + "-targets");
                ((Button)target.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            target.Picked.Should().Equal(("奥术", 1));
            source.Slots[0].CandidateEquipments.Single().IsPriority.Should().BeTrue();
            source.Strategies[0].Tags.Should().Equal(StrategyTag.阵容核心, StrategyTag.优选);
        }
        var clear = new StrategyPickerWindow(strategies, [new() { StrategyId = "shared-unknown", Tags = [StrategyTag.优选] }]);
        Run(clear, false, () =>
        {
            Descendants<Button>(clear).Single(b => Equals(b.Content, "全部取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Descendants<Button>(clear).Single(b => Equals(b.Content, "完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        clear.Picked.Should().BeEmpty("explicitly clearing strategies also clears unrecognised saved strategies");
    }

    public static void VerifyStrategyOrder()
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var strategies = data.LoadStrategiesAsync().GetAwaiter().GetResult().Take(9).ToList();
        List<LineupStrategySlot> saved =
        [
            new() { StrategyId = strategies[6].Id },
            new() { StrategyId = strategies[3].Id, Tags = [StrategyTag.次选] },
            new() { StrategyId = strategies[5].Id, Tags = [StrategyTag.次选, StrategyTag.阵容核心] },
            new() { StrategyId = strategies[4].Id, Tags = [StrategyTag.优选] },
            new() { StrategyId = strategies[1].Id, Tags = [StrategyTag.阵容核心] },
            new() { StrategyId = strategies[2].Id }
        ];
        var dialog = new StrategyPickerWindow(strategies, saved);
        var expected = new[] { 1, 5, 4, 3, 2, 6, 0, 7, 8 }.Select(i => strategies[i].Id).ToArray();
        Run(dialog, true, () =>
        {
            var rows = dialog.ItemsList.Items.Cast<StrategyPickRow>().ToList();
            rows.Select(r => r.Id).Should().Equal(expected);
            dialog.ChosenList.Items.Cast<StrategyPickRow>().Select(r => r.Id).Should().Equal(expected.Take(6));
            rows[1].IsCore.Should().BeTrue();
            dialog.KindList.SelectedIndex = 2;
            dialog.ItemsList.Items.Cast<StrategyPickRow>().Should().Equal(rows.Where(r => r.IsActive));
            dialog.KindList.SelectedIndex = 0;
            dialog.SearchBox.Text = rows[0].Name;
            dialog.ItemsList.Items.Cast<StrategyPickRow>().First().Should().BeSameAs(rows[0]);
            dialog.SearchBox.Clear(); Pump();
            var item = (ListBoxItem)dialog.ItemsList.ItemContainerGenerator.ContainerFromItem(rows[0]);
            var second = Descendants<ToggleButton>(item).Single(b => Equals(b.Content, "次选"));
            ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(second)).Toggle();
            rows[0].TagText.Should().Be("次选");
            Pump();
            Descendants<TextBlock>(dialog.ChosenList).Should().Contain(b => b.DataContext == rows[0] && b.Text == "次选");
            dialog.ItemsList.Items.Cast<StrategyPickRow>().Select(r => r.Id).Should().Equal(expected);
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "完成")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        dialog.Picked.Select(s => s.StrategyId).Should().Equal(saved.Select(s => s.StrategyId));
        var reopened = new StrategyPickerWindow(strategies, dialog.Picked);
        Run(reopened, true, () =>
        {
            var rows = reopened.ItemsList.Items.Cast<StrategyPickRow>().ToList();
            rows.Take(4).Select(r => r.Id).Should().Equal(new[] { 5, 4, 1, 3 }.Select(i => strategies[i].Id));
            rows[2].IsSecond.Should().BeTrue();
            rows[0].Tag = StrategyTag.次选;
            MouseClick((ListBoxItem)reopened.ItemsList.ItemContainerGenerator.ContainerFromItem(rows[2]));
            reopened.Close();
        });
        dialog.Picked.Single(s => s.StrategyId == strategies[5].Id).Tags.Should().Equal(StrategyTag.次选, StrategyTag.阵容核心);
        dialog.Picked.Single(s => s.StrategyId == strategies[1].Id).Tags.Should().Equal(StrategyTag.次选);
        var plain = new StrategyPickerWindow(strategies, saved.Where(s => s.Tags.Count == 0));
        plain.ItemsList.Items.Cast<StrategyPickRow>().Take(2).Select(r => r.Id).Should().Equal(strategies[2].Id, strategies[6].Id);
        plain.Close();
    }

    public static void VerifyStrategyViewport(bool night)
    {
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var strategies = data.LoadStrategiesAsync().GetAwaiter().GetResult();
        ThemeManager.Apply(GameShopTheme.Closure, night);
        WindowUiScale.Initialize(new SidebarAppearance { UiScale = 1.2 });
        foreach (var wide in new[] { true, false })
        {
            var dialog = new StrategyPickerWindow(strategies, []);
            Run(dialog, wide, () =>
            {
                var list = dialog.ItemsList;
                var row = list.Items.Cast<StrategyPickRow>().Single(r => r.Name == "众志合一");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                var viewport = Descendants<ScrollContentPresenter>(list).Single();
                MouseClick(Descendants<TextBlock>(item).Single(t => t.Text == row.Name));
                Pump();
                row.IsChosen.Should().BeTrue();
                double CheckButtons()
                {
                    var current = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
                    var buttons = Descendants<ToggleButton>(current).ToArray();
                    buttons.Select(b => b.Content).Should().Equal("核心", "优选", "次选");
                    foreach (var button in buttons)
                    {
                        button.IsVisible.Should().BeTrue();
                        var left = button.TranslatePoint(new Point(), viewport).X;
                        left.Should().BeGreaterThanOrEqualTo(0);
                        (left + button.ActualWidth).Should().BeLessThanOrEqualTo(viewport.ActualWidth + 0.5,
                            $"{button.Content} must fit before the scrollbar at width {dialog.ActualWidth}");
                        var middle = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), viewport);
                        var hit = viewport.InputHitTest(middle) as DependencyObject;
                        (hit == button || hit != null && button.IsAncestorOf(hit)).Should().BeTrue();
                    }
                    return buttons[2].TranslatePoint(new Point(), viewport).X;
                }
                Save(dialog, $"strategy-first-{night}-{dialog.Width}");
                var first = CheckButtons();
                var second = Descendants<ToggleButton>(item).Single(b => Equals(b.Content, "次选"));
                MouseClick(second);
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(second)).Toggle();
                row.IsChosen.Should().BeTrue();
                row.Tag.Should().Be(StrategyTag.次选);
                list.ScrollIntoView(list.Items[list.Items.Count - 1]); Pump();
                list.ScrollIntoView(row); Pump();
                CheckButtons().Should().BeApproximately(first, 1);
                dialog.KindList.SelectedIndex = 1; Pump();
                CheckButtons().Should().BeApproximately(first, 1);
                dialog.Close();
            });
        }
    }

    private static void Run(Window dialog, bool wide, Action action)
    {
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.Opacity = 0;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -20000; dialog.Top = -20000;
        if (!wide) dialog.Width = dialog.MinWidth;
        var operation = dialog.Dispatcher.InvokeAsync(() =>
        {
            try { Pump(); action(); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        dialog.ShowDialog();
        operation.Task.GetAwaiter().GetResult();
    }

    private static void Save(Window dialog, string name)
    {
        Pump();
        var content = (FrameworkElement)dialog.Content;
        var output = Path.Combine(TestOutput.Directory, "editor-redesign");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        foreach (var button in Descendants<Button>(content).Where(b => b.IsVisible && (b.Name is "SaveButton" or "ConfirmButton" || Equals(b.Content, "完成"))))
        {
            var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            bounds.Left.Should().BeGreaterThanOrEqualTo(0);
            bounds.Right.Should().BeLessThanOrEqualTo(content.ActualWidth + 0.5);
        }
    }

    private static void MouseClick(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
