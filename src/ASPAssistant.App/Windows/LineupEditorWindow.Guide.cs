// Added for Hongxue Stronghold Companion on 2026-11-16; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace ASPAssistant.App.Windows;

public partial class LineupEditorWindow
{
    private void RefreshGuideContent()
    {
        GuideContentHost.Children.Clear();
        GuideContentHost.Visibility = Visibility.Visible;
        var stage = _vm.CurrentStage;
        if (stage.ActiveCovenants.Count > 0)
        {
            var bonds = Text("攻略标注盟约：", 11, "TextSecondaryBrush");
            Controls.PanelToolTip.AppendNames(bonds, stage.ActiveCovenants, "、");
            bonds.Margin = new Thickness(0, 0, 0, 6); bonds.TextWrapping = TextWrapping.Wrap;
            GuideContentHost.Children.Add(bonds);
        }
        var equipment = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) };
        foreach (var name in stage.RecommendedEquipments)
        {
            var recipients = _vm.Slots.Where(s => s.Equipments.Contains(name)).Select(s => s.OperatorName).ToList();
            var button = new Button { Content = name + (recipients.Count > 0 ? " · " + string.Join("、", recipients) : " · 未分配"),
                Style = (Style)FindResource("CandidateChip"), Margin = new Thickness(0, 0, 4, 4),
                ToolTip = "点击分配给本阶段干员；仅修改计划装备，不代表本局实际持有。右键移除推荐。" };
            button.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var (slot, index) in _vm.Slots.Select((s, i) => (s, i)).Where(x => x.s.OperatorName.Length > 0))
                {
                    bool assigned = slot.Equipments.Contains(name);
                    var choice = new MenuItem { Header = slot.OperatorName, IsCheckable = true, IsChecked = assigned,
                        IsEnabled = assigned || slot.Equipments.Count < LineupEquipmentService.Capacity(slot) };
                    choice.Click += (_, _) => { _vm.ToggleSlotEquipment(index, name); RefreshEditor(); };
                    menu.Items.Add(choice);
                }
                menu.PlacementTarget = button;
                menu.IsOpen = true;
            };
            var remove = new MenuItem { Header = "移除推荐装备" };
            remove.Click += (_, _) => { stage.RecommendedEquipments.Remove(name); RefreshEditor(); };
            button.ContextMenu = new ContextMenu { Items = { remove } };
            equipment.Children.Add(button);
        }
        var addEquipment = new Button { Content = "＋ 推荐装备", Style = (Style)FindResource("CandidateChip"), Margin = new Thickness(0, 0, 4, 4) };
        addEquipment.Click += (_, _) =>
        {
            var picker = new EquipmentPickerWindow(_vm.AllEquipment, _vm.EquipmentRules, "本阶段推荐装备",
                stage.RecommendedEquipments.Select(n => new CandidateEquipment { Name = n }), []) { Owner = this };
            if (picker.ShowDialog() != true || picker.Candidates is null) return;
            stage.RecommendedEquipments = picker.Candidates.Select(c => c.Name).ToList();
            RefreshEditor();
        };
        equipment.Children.Add(addEquipment);
        var equipmentSection = new Expander { Header = $"推荐装备 {stage.RecommendedEquipments.Count} 件 · 点击分配", IsExpanded = stage.RecommendedEquipments.Count <= 6, Content = equipment, Margin = new Thickness(0, 4, 0, 4) };
        equipmentSection.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        GuideContentHost.Children.Add(equipmentSection);
        var notes = new TextBox { Text = stage.OperationIdea, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 85, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 4) };
        notes.SetResourceReference(BackgroundProperty, "CardBackgroundBrush");
        notes.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        notes.SetResourceReference(BorderBrushProperty, "LineBrush");
        notes.TextChanged += (_, _) => _vm.SetStageNotes(notes.Text);
        var noteSection = new Expander { Header = "攻略讲解 · 可编辑", Content = notes, Margin = new Thickness(0, 2, 0, 6) };
        noteSection.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        GuideContentHost.Children.Add(noteSection);
    }

    private void RefreshSourcePage()
    {
        GuideSourceMetadata.Visibility = _vm.Guide is null ? Visibility.Collapsed : Visibility.Visible;
        if (_vm.Guide is not { } guide) return;
        GuideAuthorText.Text = $"作者：{guide.Author} · {guide.SeasonName}";
        GuideScopeText.Text = guide.ApplicableScope.Length > 0 ? "适用范围：" + guide.ApplicableScope : "";
    }

    private void OnEditorPageClick(object sender, RoutedEventArgs e)
    {
        bool source = ReferenceEquals(sender, SourcePageButton);
        LineupPage.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        GuideSourcePage.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        LineupPageButton.Tag = source ? null : "active";
        SourcePageButton.Tag = source ? "active" : null;
    }

    private void AddGuideSlotControls(StackPanel stack, int index, LineupSlot slot)
    {
        if (slot.GuideLoadout is not { } loadout) return;
        foreach (var skill in new[] { true, false })
        {
            var choices = new[] { new SklandChoice("", "未指定") }.Concat(skill ? loadout.Skills : loadout.Modules).ToList();
            var box = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedValuePath = "Id",
                SelectedValue = skill ? loadout.SkillId : loadout.ModuleId, MinHeight = 26, FontSize = 10.5,
                Margin = new Thickness(0, 4, 0, 0), ToolTip = skill ? "攻略选择的技能；仅保存到本阵容" : "攻略选择的模组；仅保存到本阵容" };
            box.SelectionChanged += (_, _) =>
            {
                var current = _vm.Slots[index].GuideLoadout!;
                _vm.SetGuideLoadout(index, skill ? (string)box.SelectedValue : current.SkillId,
                    skill ? current.ModuleId : (string)box.SelectedValue);
            };
            var row = new DockPanel();
            var label = Text(skill ? "技能 " : "模组 ", 10.5, "TextSecondaryBrush");
            label.Margin = new Thickness(0, 4, 4, 0); DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label); row.Children.Add(box); stack.Children.Add(row);
        }
    }
}
