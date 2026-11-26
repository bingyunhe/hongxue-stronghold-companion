using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ASPAssistant.App;
using ASPAssistant.App.Controls;
using ASPAssistant.App.Views;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;
using FluentAssertions;

namespace ASPAssistant.Tests.Views;

internal static class LineupFeatureTests
{
    public static void Verify()
    {
        Operator[] operators = [new() { Name = "荒芜拉普兰德", Tier = 6, CoreCovenants = ["叙拉古"] },
            new() { Name = "角峰", Tier = 1, CoreCovenants = ["谢拉格"] }];
        var calculator = new CovenantCalculator(operators, [], new Dictionary<string, string>());
        var store = new LineupStore(Path.Combine(Path.GetTempPath(), "unused-lineup-ui"), calculator.EquipmentRules);
        foreach (var theme in Enum.GetValues<GameShopTheme>())
        foreach (var night in new[] { false, true })
        {
            ThemeManager.Apply(theme, night);
            var source = new Lineup { Name = "后期可用阵容", Slots = [new() { OperatorName = "荒芜拉普兰德" }],
                EarlySlots = [new() { OperatorName = "角峰" }] };
            var banned = new Lineup { Name = "后期不可用阵容", Slots = [new() { OperatorName = "角峰" }] };
            var vm = new LineupViewModel(store, calculator);
            vm.LoadLineups([source, banned]);
            vm.SetRunInfo(name => name == "角峰", new HashSet<string>());
            var view = new LineupListView { DataContext = vm };
            var host = new Window { Content = view, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 360, Height = 760 };
            try
            {
                host.Show();
                Pump();
                var filter = (ListBox)view.FindName("AvailabilityFilterList");
                filter.SelectedIndex.Should().Be(0);
                filter.SelectedIndex = 1;
                Pump();
                vm.VisibleLineups.Should().Equal(source);
                filter.SelectedIndex = 2;
                Pump();
                vm.VisibleLineups.Should().Equal(banned);
                filter.SelectedIndex = 0;
                Pump();
                foreach (var width in new[] { 280, 360 })
                {
                    host.Width = width;
                    Pump();
                    var items = (ItemsControl)view.FindName("LineupItems");
                    items.Items.Count.Should().Be(2);
                    var moveUp = Descendants<Button>(items).Where(b => Equals(b.ToolTip, "上移阵容")).ToList();
                    moveUp[0].IsEnabled.Should().BeFalse();
                    moveUp[1].IsEnabled.Should().BeTrue();
                    var moveDown = Descendants<Button>(items).Where(b => Equals(b.ToolTip, "下移阵容")).ToList();
                    moveDown[0].IsEnabled.Should().BeTrue();
                    moveDown[1].IsEnabled.Should().BeFalse();
                    foreach (var button in Descendants<Button>(items))
                    {
                        var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                        bounds.Right.Should().BeLessThanOrEqualTo(view.ActualWidth + 1, "card actions must fit the sidebar");
                    }
                    Save(view, $"{theme}-{night}-lineups-{width}");
                }
                (Lineup? Lineup, int Direction) requested = (null, 9);
                view.MoveLineupRequested += (lineup, direction) => requested = (lineup, direction);
                Descendants<Button>(view).Last(b => Equals(b.ToolTip, "置顶阵容"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                requested.Should().Be((banned, 0));
                vm.SetRunInfo(_ => false, new HashSet<string>());
                filter.SelectedIndex = 3;
                Pump();
                ((TextBlock)view.FindName("NoMatchHint")).Text.Should().Be("没有符合条件的阵容");
                ((TextBlock)view.FindName("NoMatchHint")).Visibility.Should().Be(Visibility.Visible);
            }
            finally { host.Close(); }

            var editorVm = new LineupEditorViewModel(source, operators, [], calculator);
            var editor = new LineupEditorWindow(editorVm, []);
            try
            {
                var content = (FrameworkElement)editor.Content;
                content.Measure(new Size(1000, 740));
                content.Arrange(new Rect(0, 0, 1000, 740));
                content.UpdateLayout();
                var elite = Descendants<CheckBox>(content).Single(check => Equals(check.Content, "精锐"));
                elite.IsChecked = true;
                editorVm.Slots[0].IsElite.Should().BeTrue();
                source.Slots[0].IsElite.Should().BeFalse("the editor modifies a draft");
                editorVm.ToLineup().Slots[0].IsElite.Should().BeTrue();
                content.UpdateLayout();
                Save(content, $"{theme}-{night}-editor");
                Descendants<CheckBox>(content).Single(check => Equals(check.Content, "精锐")).IsChecked = false;
                editorVm.Slots[0].IsElite.Should().BeFalse();
            }
            finally { editor.Close(); }

            var candidates = Enumerable.Range(1, 14).Select(i => new Operator { Name = $"自选候选{i:D2}", IsCustom = true,
                Tier = 5, CoreCovenants = ["谢拉格"], IsSelectedCustom = i <= 12 }).ToList();
            var browseVm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Default };
            browseVm.LoadOperators([.. operators, .. candidates]);
            var browse = new OperatorBrowseView { DataContext = browseVm };
            var browseHost = new Window { Content = browse, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 280, Height = 900 };
            try
            {
                browseHost.Show();
                Pump();
                browseVm.FilteredOperators.Should().HaveCount(2);
                var sourceTabs = (ListBox)browse.FindName("OperatorSourceList");
                sourceTabs.SelectedIndex.Should().Be(0);
                Save(browse, $"{theme}-{night}-default-operators-280");
                sourceTabs.SelectedIndex = 1;
                Pump();
                browseVm.FilteredOperators.Should().HaveCount(12).And.OnlyContain(op => op.IsCustom);
                ((Button)browse.FindName("ManageCustomOperatorsButton")).Visibility.Should().Be(Visibility.Visible);
                ((TextBlock)((Button)browse.FindName("ManageCustomOperatorsButton")).Content).Text.Should().Be("管理自选 · 12/12");
                Save(browse, $"{theme}-{night}-custom-operators-280");
                var picker = new OperatorPickerWindow([.. operators, .. candidates], "", new HashSet<string>());
                try
                {
                    var pickerContent = (FrameworkElement)picker.Content;
                    pickerContent.Measure(new Size(778, 698));
                    pickerContent.Arrange(new Rect(0, 0, 778, 698));
                    pickerContent.UpdateLayout();
                    ((ListBox)picker.FindName("OperatorsList")).Items.Count.Should().Be(14);
                }
                finally { picker.Close(); }
            }
            finally { browseHost.Close(); }
            var selection = new CustomOperatorSelectionWindow(candidates);
            try
            {
                var content = (FrameworkElement)selection.Content;
                content.Measure(new Size(618, 658));
                content.Arrange(new Rect(0, 0, 618, 658));
                content.UpdateLayout();
                var checks = Descendants<CheckBox>(content).ToList();
                checks.Count(check => check.IsChecked == true).Should().Be(12);
                checks.Where(check => check.IsChecked != true).Should().OnlyContain(check => !check.IsEnabled);
                checks[0].IsChecked = false;
                checks.Should().OnlyContain(check => check.IsEnabled);
                checks[12].IsChecked = true;
                ((TextBox)selection.FindName("SearchBox")).Text = "候选13";
                Save(content, $"{theme}-{night}-custom-selection");
                Descendants<Button>(content).Single(button => Equals(button.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                selection.Result.Should().HaveCount(12).And.Contain("自选候选13").And.NotContain("自选候选01");
                candidates[0].IsSelectedCustom.Should().BeTrue("dialog edits do not mutate the saved selection");
            }
            finally { selection.Close(); }
        }
        ThemeManager.Apply(GameShopTheme.Terminal, true);
    }

    public static void VerifyTierMouse()
    {
        var ops = Enumerable.Range(0,12).Select(i => new Operator { Name = $"候选{i:D2}", IsCustom = true }).ToList();
        var initial = ops.Take(10).Select((op,i) => new CustomRosterEntry(op.Name, i < 5 ? 5 : 6, Slot: i % 5)).ToList();
        var dialog = new CustomOperatorSelectionWindow(ops, initial)
            { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            dialog.Show();
            Pump();
            var row = dialog.ChoicesPanel.Children[0];
            var slider = Descendants<ListBox>(row).Single();
            void ClickTier(ListBox control, int index, bool nearEdge = false)
            {
                var item = (ListBoxItem)control.Items[index];
                item.BringIntoView();
                Pump();
                var point = item.TranslatePoint(new Point(nearEdge ? 2 : item.ActualWidth / 2, item.ActualHeight / 2), dialog);
                var hit = (UIElement)dialog.InputHitTest(point);
                item.IsAncestorOf(hit).Should().BeTrue();
                hit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Pump();
            }
            ClickTier(slider, 1, true);
            dialog.TierCount.Text.Should().Contain("V 阶 4/6").And.Contain("VI 阶 6/6");
            dialog.ChoicesPanel.Children[0].Should().BeSameAs(row);
            var nextSlider = Descendants<ListBox>(dialog.ChoicesPanel.Children[1]).Single();
            ClickTier(nextSlider, 1);
            nextSlider.SelectedIndex.Should().Be(0, "the destination tier is full");
            ClickTier(slider, 0);
            dialog.TierCount.Text.Should().Contain("V 阶 5/6").And.Contain("VI 阶 5/6");
            dialog.ResetDraftButton.IsEnabled.Should().BeFalse();
            var sixthRow = dialog.ChoicesPanel.Children[5];
            ClickTier(Descendants<ListBox>(sixthRow).Single(), 0);
            dialog.TierCount.Text.Should().Contain("V 阶 6/6").And.Contain("VI 阶 4/6");
            dialog.ChoicesPanel.Children[5].Should().BeSameAs(sixthRow);
            ClickTier(slider, 1);
            Descendants<Button>(dialog).Single(t => Equals(t.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Entries!.Single(e => e.Name == ops[0].Name).Slot.Should().BeNull();
            dialog.Entries!.Single(e => e.Name == ops[1].Name).Slot.Should().Be(1);
            var reopened = new CustomOperatorSelectionWindow(ops, dialog.Entries);
            try
            {
                reopened.ChoicesPanel.Children.Cast<Border>().Select(c => ((Operator)c.Tag).Name)
                    .Should().Equal(ops.Skip(1).Take(5).Concat(ops.Take(1)).Concat(ops.Skip(6)).Select(o => o.Name));
                reopened.ResetDraftButton.IsEnabled.Should().BeFalse();
            }
            finally { reopened.Close(); }
        }
        finally { dialog.Close(); }
    }

    public static void VerifyCustomFilterOrder()
    {
        var skill = new CustomSkillOption("selected", new() { Name = "技能甲" }, new(), []);
        var ops = "FEDCBA".Select(c => new Operator { Name = c.ToString(), IsCustom = true, SkillOptions = [skill],
            CoreCovenants = "ABD".Contains(c) ? ["炎"] : [],
            AdditionalCovenants = "AD".Contains(c) ? ["精准"] : [] }).ToList();
        var dialog = new CustomOperatorSelectionWindow(ops, [new("B",6),new("C",5),new("F"),new("E",5)])
            { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        IEnumerable<Border> Cards() => dialog.ChoicesPanel.Children.Cast<Border>();
        Border Card(string name) => Cards().Single(c => ((Operator)c.Tag).Name == name);
        void Order(string expected)
        {
            Pump();
            string.Concat(Cards().Where(c => c.IsVisible).Select(c => ((Operator)c.Tag).Name)).Should().Be(expected);
        }
        try
        {
            dialog.Show();
            Order("CEBFAD");
            var b = Card("B");
            Descendants<ComboBox>(b).Single().SelectedIndex = 1;
            Descendants<ListBox>(Card("A")).Single().SelectedIndex = 1;
            Order("CEABFD");
            Descendants<ListBox>(b).Single().SelectedIndex = 0;
            Order("CEABFD");
            Descendants<ComboBox>(b).Single().SelectedIndex.Should().Be(1);
            dialog.FilterToggle.IsChecked = true;
            Pump();
            var toggles = Descendants<System.Windows.Controls.Primitives.ToggleButton>(dialog)
                .Where(t => t.DataContext is FilterChip).ToList();
            toggles.Single(t => ((FilterChip)t.DataContext).Value == "炎").IsChecked = true;
            Order("ABD");
            toggles.Single(t => ((FilterChip)t.DataContext).Value == "精准").IsChecked = true;
            Order("AD");
            dialog.SearchBox.Text = "D";
            Order("D");
            dialog.SearchBox.Text = "";
            dialog.FilterToggle.IsChecked = false;
            Pump();
            var remove = Descendants<Button>(dialog.ActiveFilters).Single(t => ((FilterChip)t.DataContext).Value == "精准");
            remove.IsVisible.Should().BeTrue();
            remove.Command.Execute(remove.CommandParameter);
            Order("ABD");
            var last = Descendants<Button>(dialog.ActiveFilters).Single();
            last.Command.Execute(last.CommandParameter);
            Descendants<CheckBox>(b).Single().IsChecked = false;
            Order("CEABFD");
            dialog.FilterToggle.IsChecked = true;
            Pump();
            toggles.Single(t => ((FilterChip)t.DataContext).Value == "炎").IsChecked = true;
            dialog.SearchBox.Text = "B";
            Order("B");
            dialog.ResetDraftButton.IsEnabled.Should().BeTrue();
            dialog.ResetDraftButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Order("B");
            dialog.ResetDraftButton.IsEnabled.Should().BeFalse();
            dialog.SearchBox.Text.Should().Be("B");
            Descendants<CheckBox>(b).Single().IsChecked.Should().BeTrue();
            Descendants<ListBox>(b).Single().SelectedIndex.Should().Be(1);
            Descendants<ComboBox>(b).Single().SelectedIndex.Should().Be(0);
            dialog.SearchBox.Text = "";
            Order("BAD");
            toggles.Single(t => ((FilterChip)t.DataContext).Value == "炎").IsChecked = false;
            Order("CEBFAD");
            var a = Descendants<CheckBox>(Card("A")).Single();
            a.IsChecked = true;
            Order("ACEBFD");
            a.IsChecked = false;
            Order("ACEBFD");
            dialog.ResetDraftButton.IsEnabled.Should().BeTrue();
            dialog.ResetDraftButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Order("CEBFAD");
            var clear = Descendants<Button>(dialog).Single(t => Equals(t.Content,"清空选择"));
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Order("CEBFAD");
            dialog.SelectionCount.Text.Should().Contain("0 / 12");
            dialog.ResetDraftButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.SelectionCount.Text.Should().Contain("4 / 12");
            dialog.ResetDraftButton.IsEnabled.Should().BeFalse();
            ops.Should().OnlyContain(o => o.RosterEntry == null);
            Descendants<Button>(dialog).Single(t => Equals(t.Content,"取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Entries.Should().BeNull();
        }
        finally { dialog.Close(); }
    }

    public static void VerifyCustomCarry()
    {
        var folder = Directory.CreateTempSubdirectory("custom-carry-ui-").FullName;
        var data = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "data"));
        var customs = data.LoadCustomOperatorsAsync().GetAwaiter().GetResult();
        string[] names = ["予愿安洁莉娜","假日威龙陈","电弧","阿","丰川祥子"];
        var ops = names.Select(n => customs.Single(op => op.Name == n)).ToList();
        foreach (var (op,index) in ops.Select((o,i) => (o,i)))
            op.SetRosterEntry(new(op.Name, index < 3 ? 5 : 6, op.SkillOptions.Last().Id));
        var calc = new CovenantCalculator(ops, [], new Dictionary<string,string>());
        var vm = new OperatorBrowseViewModel { SelectedOperatorSource = OperatorSourceFilter.Custom };
        var panel = new SidePanelWindow(vm, new(), new(), new(), new(), new(),
            new(new LineupStore(Path.Combine(folder,"lineups"),calc.EquipmentRules),calc), new(calc.EquipmentRules,Path.Combine(folder,"share")),
            ops, [], [], new(folder), GameShopTheme.Terminal, new() { FreeMovement = true })
            { Width = 430, Height = 820, Top = -20000, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
        var state = new DeployPoolState();
        try
        {
            panel.Show();
            ((TabControl)panel.FindName("MainTabs")).SelectedItem = panel.FindName("CodexTab");
            panel.InitCustomRun(state);
            panel.StartCustomRun();
            var browse = (OperatorBrowseView)panel.FindName("OperatorView");
            Button Carry(string name)
            {
                Pump();
                var card = Descendants<OperatorCard>(browse).First(c => c.IsVisible && ((Operator)c.DataContext).Name == name);
                var button = (Button)card.FindName("CarryButton");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return button;
            }
            panel.TrackingVm.TrackLineup(new Lineup { Id = "自选验证", Name = "自选验证", Slots = [new() { OperatorName = names[0] }, new() { OperatorName = names[4] }] });
            vm.CarryStatus.Should().Contain("未识别，按 0 人");
            panel.ApplyCustomOpening(new(CustomRosterPage.Prepared, [], []) { ConfirmedEmpty = true });
            vm.CarryStatus.Should().Contain("已确认未携带");
            panel.ApplyCustomOpening(new(CustomRosterPage.Choosing, [], []));
            panel.CarryAdviceText.Text.Should().Contain(names[0]).And.Contain(names[4]);
            state.CarriedCustom.Should().BeEmpty();
            var read = new CustomRosterReading(CustomRosterPage.Prepared, [ops[0].RosterEntry!, ops[4].RosterEntry!], []);
            panel.ApplyCustomOpening(read);
            Carry(names[1]);
            var menu = Carry(names[2]).ContextMenu;
            menu.IsOpen.Should().BeTrue();
            var choices = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled).ToList();
            choices.Select(item => (string)item.Tag).Should().BeEquivalentTo(new[] { names[0], names[1] });
            foreach (var night in new[] { false, true })
            {
                ThemeManager.Apply(GameShopTheme.Closure, night);
                Pump();
                Save(menu, $"carry-replacement-Closure-{night}");
            }
            choices.Single(item => Equals(item.Tag, names[0])).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            state.CarriedCustom!.Select(e => e.Name).Should().BeEquivalentTo(names[2], names[1], names[4]);
            var shopping = ((ItemsControl)panel.TrackingView.FindName("FinalCells")).Items.Cast<TrackedCell>();
            shopping.Select(c => c.Name).Should().Contain(names[4]).And.NotContain(names[0]);
            panel.TrackingVm.TrackedOperators.Select(entry => entry.Name).Should().Contain(names[0], "the saved target survives a run correction");
            ops[0].IsAvailableForPlanning.Should().BeFalse();
            ops[2].IsAvailableForPlanning.Should().BeTrue();
            vm.CarryStatus.Should().Contain("手动确认");
            panel.ApplyCustomOpening(read);
            state.CarriedCustom.Should().HaveCount(3);
            panel.ApplyCustomOpening(new(CustomRosterPage.Prepared, [ops[0].RosterEntry!], [(6,1)]));
            state.CarriedCustom.Should().HaveCount(3, "a delayed partial read must preserve a manual correction");
            Carry(names[1]);
            panel.ApplyCustomOpening(new(CustomRosterPage.Choosing, [], []));
            state.CarriedCustom.Should().BeEmpty();
            panel.ApplyCustomOpening(read);
            foreach (var theme in Enum.GetValues<GameShopTheme>())
            foreach (var night in new[]{false,true})
            {
                ThemeManager.Apply(theme,night);
                Pump();
                var adviceButton = Descendants<Button>(browse).Single(b => Equals(b.Content, "开局建议"));
                adviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Save((FrameworkElement)panel.Content, $"carry-advice-{theme}-{night}");
                Descendants<Button>(panel.CarryAdviceBanner).Single(b => Equals(b.Content, "收起")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Save(browse, $"roster-{theme}-{night}-catalogue");
                if (theme == GameShopTheme.Closure)
                {
                    panel.Width = 280;
                    var layout = (Button)browse.FindName("LayoutButton");
                    foreach (var columns in new[]{2,3,1})
                    {
                        layout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump();
                        var card = Descendants<OperatorCard>(browse).First(c => c.IsVisible);
                        var carry = (Button)card.FindName("CarryButton");
                        var end = carry.TranslatePoint(new Point(carry.ActualWidth,0),card);
                        end.X.Should().BeLessThanOrEqualTo(card.ActualWidth);
                        if (columns != 1) Save(browse,$"roster-{theme}-{night}-280-{columns}");
                    }
                    panel.Width = 430;
                    Pump();
                }
                var dialog = new CustomOperatorSelectionWindow(ops) {Owner=panel, ShowActivated=false, ShowInTaskbar=false};
                try
                {
                    dialog.Show(); Pump();
                    var draft = Descendants<ListBox>(dialog).First(lb => lb.Items.Count == 2 && lb.Items[0] is ListBoxItem { Content: "V" });
                    var before = draft.SelectedIndex;
                    draft.SelectedIndex = 1-before;
                    Pump();
                    draft.SelectedItems.Count.Should().Be(1);
                    Save((FrameworkElement)dialog.Content, $"roster-{theme}-{night}-editor");
                    dialog.FilterToggle.IsChecked = true;
                    Pump();
                    Save((FrameworkElement)dialog.Content, $"roster-{theme}-{night}-filters");
                }
                finally { dialog.Close(); }
                var first = Descendants<OperatorCard>(browse).First(c => c.IsVisible);
                var op = (Operator)first.DataContext;
                var tooltip = (ToolTip)first.FindName("DetailsToolTip");
                tooltip.PlacementTarget = first;
                ((ContentControl)first.FindName("DetailsHost")).Content = op;
                tooltip.IsOpen = true;
                Pump();
                var contents = (FrameworkElement)tooltip.Content;
                Descendants<TextBlock>(contents).Should().Contain(t => t.Text == op.Normal.Skill!.Description);
                Save(contents, $"roster-{theme}-{night}-skill");
                tooltip.IsOpen = false;
                var tabs = (TabControl)panel.FindName("MainTabs");
                tabs.SelectedIndex = 1;
                Pump();
                var transition = System.Diagnostics.Stopwatch.StartNew();
                while (panel.TrackingView.Opacity < 0.999 && transition.ElapsedMilliseconds < 1500)
                {
                    Thread.Sleep(12);
                    Pump();
                }
                panel.TrackingView.Opacity.Should().BeApproximately(1,0.001);
                var purchase = (ItemsControl)panel.TrackingView.FindName("FinalCells");
                var tiers = Descendants<TextBlock>(purchase).Where(t => t.Text is "Ⅴ" or "Ⅵ").Select(t => t.Text).ToList();
                tiers.Should().BeEquivalentTo("Ⅴ","Ⅵ");
                Save(panel.TrackingView, $"roster-{theme}-{night}-purchase");
                tabs.SelectedItem = panel.FindName("CodexTab");
                Pump();
            }
        }
        finally { panel.Close(); ThemeManager.Apply(GameShopTheme.Terminal,true); }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Save(FrameworkElement content, string name)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(TestOutput.Directory, "lineup-feature-previews");
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }
}
