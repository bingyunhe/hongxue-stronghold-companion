using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ASPAssistant.Core;
using ASPAssistant.Core.Interop;
using ASPAssistant.Core.Models;

namespace ASPAssistant.App.Windows;

public partial class SidePanelWindow
{
    private enum HotkeyAction { Assistant, Recognition, Guide }
    private sealed class Shortcut(int first, int second, string label)
    {
        public int Id = first;
        public int NextId { get; private set; } = second;
        public void UseNextId() => (Id, NextId) = (NextId, Id);
        public string Label => label;
        public AssistantHotkey? Key;
    }
    private readonly Dictionary<HotkeyAction, Shortcut> _shortcuts = new()
    {
        [HotkeyAction.Assistant] = new(0x4154, 0x4155, "隐藏 / 显示助手"),
        [HotkeyAction.Recognition] = new(HotkeyIdManualScan, 0x4156, "快速识别"),
        [HotkeyAction.Guide] = new(0x4157, 0x4158, "隐藏 / 显示攻略讲解")
    };
    internal int AssistantShortcutId => _shortcuts[HotkeyAction.Assistant].Id;
    internal int GuideShortcutId => _shortcuts[HotkeyAction.Guide].Id;
    internal int RecognitionShortcutId => _shortcuts[HotkeyAction.Recognition].Id;
    private bool _savingHotkey;
    public bool IsAssistantHidden { get; internal set; }
    public event Action? ToggleAssistantVisibilityRequested;

    private (TextBox Box, TextBlock Status, Button Clear) ControlsFor(HotkeyAction action) => action switch
    {
        HotkeyAction.Assistant => (AssistantHotkeyBox, AssistantHotkeyStatus, ClearHotkeyButton),
        HotkeyAction.Recognition => (RecognitionHotkeyBox, RecognitionHotkeyStatus, ClearRecognitionHotkeyButton),
        _ => (GuideHotkeyBox, GuideHotkeyStatus, ClearGuideHotkeyButton)
    };

    public Task InitAssistantHotkeyAsync() => InitHotkeyAsync(HotkeyAction.Assistant);
    public Task InitRecognitionHotkeyAsync() => InitHotkeyAsync(HotkeyAction.Recognition);
    public Task InitGuideHotkeyAsync() => InitHotkeyAsync(HotkeyAction.Guide);

    private string? Conflict(HotkeyAction action, AssistantHotkey? key)
    {
        if (key is null) return null;
        foreach (var (other, shortcut) in _shortcuts)
            if (other != action && shortcut.Key == key) return $"与{shortcut.Label}快捷键冲突";
        return key.ConflictReason();
    }

    private async Task InitHotkeyAsync(HotkeyAction action)
    {
        var (box, status, _) = ControlsFor(action);
        try
        {
            var hotkey = await (action switch
            {
                HotkeyAction.Assistant => _settingsManager.LoadAssistantHotkeyAsync(),
                HotkeyAction.Recognition => _settingsManager.LoadRecognitionHotkeyAsync(),
                _ => _settingsManager.LoadGuideHotkeyAsync()
            });
            box.Text = hotkey is null ? "未设置（点击录入）" : HotkeyText(hotkey);
            if (action == HotkeyAction.Recognition) RunBanView.SetRecognitionHotkeyText(null);
            status.Visibility = Visibility.Collapsed;
            if (hotkey is null) return;
            var shortcut = _shortcuts[action];
            var error = Conflict(action, hotkey);
            if (error is null && TryRegisterHotkey(hotkey, shortcut.Id, out error))
            {
                shortcut.Key = hotkey;
                if (action == HotkeyAction.Recognition) RunBanView.SetRecognitionHotkeyText(HotkeyText(hotkey));
                status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                status.Text = "快捷键已启用";
                return;
            }
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            status.Text = $"保存的快捷键 {HotkeyText(hotkey)} 未生效：{error}";
            AppLogger.Warn("Hotkey", status.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            status.Text = $"无法读取快捷键：{ex.Message}，请重新设置";
            AppLogger.Warn("Hotkey", status.Text);
        }
    }

    private bool TryRegisterHotkey(AssistantHotkey hotkey, int id, out string? error)
    {
        error = null;
        if (User32.RegisterHotKey(new WindowInteropHelper(this).Handle, id, hotkey.Modifiers | 0x4000, hotkey.VirtualKey))
            return true;
        var code = Marshal.GetLastWin32Error();
        error = code == 1409 ? "该组合键已被系统或其他程序占用，请换一组" : $"Windows 无法注册快捷键（错误 {code}），请换一组";
        return false;
    }

    private async void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var action = sender == AssistantHotkeyBox ? HotkeyAction.Assistant :
            sender == RecognitionHotkeyBox ? HotkeyAction.Recognition : HotkeyAction.Guide;
        e.Handled = true;
        if (_savingHotkey || e.IsRepeat) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var hotkey = new AssistantHotkey(
            (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0)
            | (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0) | (modifiers.HasFlag(ModifierKeys.Windows) ? 8u : 0),
            (uint)KeyInterop.VirtualKeyFromKey(key));
        await SaveHotkeyAsync(hotkey, action);
    }

    private async void OnClearAssistantHotkey(object sender, RoutedEventArgs e) => await SaveAssistantHotkeyAsync(null);
    private async void OnClearRecognitionHotkey(object sender, RoutedEventArgs e) => await SaveRecognitionHotkeyAsync(null);
    private async void OnClearGuideHotkey(object sender, RoutedEventArgs e) => await SaveGuideHotkeyAsync(null);
    internal Task SaveAssistantHotkeyAsync(AssistantHotkey? key) => SaveHotkeyAsync(key, HotkeyAction.Assistant);
    internal Task SaveRecognitionHotkeyAsync(AssistantHotkey? key) => SaveHotkeyAsync(key, HotkeyAction.Recognition);
    internal Task SaveGuideHotkeyAsync(AssistantHotkey? key) => SaveHotkeyAsync(key, HotkeyAction.Guide);

    private async Task SaveHotkeyAsync(AssistantHotkey? hotkey, HotkeyAction action)
    {
        var shortcut = _shortcuts[action];
        if (_savingHotkey || (hotkey is not null && hotkey == shortcut.Key)) return;
        var (box, status, _) = ControlsFor(action);
        var error = Conflict(action, hotkey);
        var nextId = shortcut.NextId;
        if (error is not null || (hotkey is not null && !TryRegisterHotkey(hotkey, nextId, out error)))
        {
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            status.Text = error + "；原设置保留";
            return;
        }
        _savingHotkey = true;
        SetHotkeyControlsEnabled(false);
        try
        {
            await (action switch
            {
                HotkeyAction.Assistant => _settingsManager.SaveAssistantHotkeyAsync(hotkey),
                HotkeyAction.Recognition => _settingsManager.SaveRecognitionHotkeyAsync(hotkey),
                _ => _settingsManager.SaveGuideHotkeyAsync(hotkey)
            });
            ReleaseHotkey(action);
            shortcut.UseNextId();
            shortcut.Key = hotkey;
            if (action == HotkeyAction.Recognition) RunBanView.SetRecognitionHotkeyText(hotkey is null ? null : HotkeyText(hotkey));
            box.Text = hotkey is null ? "未设置（点击录入）" : HotkeyText(hotkey);
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            status.Visibility = Visibility.Collapsed;
            status.Text = hotkey is null ? "已清除快捷键" : "已保存，按下可" + shortcut.Label;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hotkey is not null) User32.UnregisterHotKey(new WindowInteropHelper(this).Handle, nextId);
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            status.Text = $"保存失败：{ex.Message}；原设置保留";
        }
        finally
        {
            _savingHotkey = false;
            SetHotkeyControlsEnabled(true);
        }
    }

    private static string HotkeyText(AssistantHotkey hotkey)
        => ((hotkey.Modifiers & 2) != 0 ? "Ctrl + " : "") + ((hotkey.Modifiers & 1) != 0 ? "Alt + " : "")
            + ((hotkey.Modifiers & 4) != 0 ? "Shift + " : "") + (hotkey.VirtualKey switch
            {
                0x6F => "小键盘 ÷", 0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
                0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
                _ => KeyInterop.KeyFromVirtualKey((int)hotkey.VirtualKey).ToString()
            });

    private void SetHotkeyControlsEnabled(bool enabled)
    {
        foreach (var action in _shortcuts.Keys)
        {
            var (box, _, clear) = ControlsFor(action);
            box.IsEnabled = clear.IsEnabled = enabled;
        }
    }

    private bool HandleShortcut(int id)
    {
        foreach (var (action, shortcut) in _shortcuts)
        {
            if (shortcut.Id != id || shortcut.Key is not { } key) continue;
            foreach (var recording in _shortcuts.Keys)
                if (IsActive && ControlsFor(recording).Box.IsKeyboardFocusWithin)
                {
                    _ = SaveHotkeyAsync(key, recording);
                    return true;
                }
            switch (action)
            {
                case HotkeyAction.Assistant: ToggleAssistantVisibilityRequested?.Invoke(); break;
                case HotkeyAction.Recognition: ManualScanRequested?.Invoke(); break;
                case HotkeyAction.Guide: ShowTrackingGuide(toggle: true); break;
            }
            return true;
        }
        return false;
    }

    public void ReleaseAssistantHotkey() => ReleaseHotkey(HotkeyAction.Assistant);
    public void ReleaseRecognitionHotkey() => ReleaseHotkey(HotkeyAction.Recognition);
    public void ReleaseGuideHotkey() => ReleaseHotkey(HotkeyAction.Guide);
    private void ReleaseHotkey(HotkeyAction action)
    {
        var shortcut = _shortcuts[action];
        if (shortcut.Key is null) return;
        User32.UnregisterHotKey(new WindowInteropHelper(this).Handle, shortcut.Id);
        shortcut.Key = null;
    }
}
