// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using FluentAssertions;

namespace ASPAssistant.Tests.Data;

public class SettingsManagerTests : IDisposable
{
    [Fact]
    public async Task AutomaticCurrentRunFiltersDefaultOffAndPersist()
    {
        (await _manager.LoadAutomaticCurrentRunFiltersAsync()).Should().BeFalse();
        await _manager.SaveAutomaticCurrentRunFiltersAsync(true);
        (await new SettingsManager(_tempDir).LoadAutomaticCurrentRunFiltersAsync()).Should().BeTrue();
        await _manager.SaveAutomaticCurrentRunFiltersAsync(false);
        (await new SettingsManager(_tempDir).LoadAutomaticCurrentRunFiltersAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RoundEnemyAutomaticReadDefaultsOffAndPersists()
    {
        (await _manager.LoadAutomaticRoundEnemiesAsync()).Should().BeFalse();
        await _manager.SaveAutomaticRoundEnemiesAsync(true);
        (await new SettingsManager(_tempDir).LoadAutomaticRoundEnemiesAsync()).Should().BeTrue();
        await _manager.SaveAutomaticRoundEnemiesAsync(false);
        (await _manager.LoadAutomaticRoundEnemiesAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(PurchaseRecognitionSpeed.Standard)]
    [InlineData(PurchaseRecognitionSpeed.Fast)]
    [InlineData(PurchaseRecognitionSpeed.Fastest)]
    public async Task PurchaseSpeedRoundTripsWithoutChangingAutomaticPurchases(PurchaseRecognitionSpeed speed)
    {
        (await _manager.LoadPurchaseRecognitionSpeedAsync()).Should().Be(PurchaseRecognitionSpeed.Standard);
        await _manager.SaveAutomaticPurchaseTrackingAsync(true);
        await _manager.SavePurchaseRecognitionSpeedAsync(speed);
        (await new SettingsManager(_tempDir).LoadPurchaseRecognitionSpeedAsync()).Should().Be(speed);
        (await _manager.LoadAutomaticPurchaseTrackingAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("\"Unknown\"")]
    [InlineData("123")]
    public async Task InvalidPurchaseSpeedReturnsToStandard(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "purchase-recognition-speed.json"), json);
        (await _manager.LoadPurchaseRecognitionSpeedAsync()).Should().Be(PurchaseRecognitionSpeed.Standard);
    }

    [Theory]
    [InlineData(MainWindowCloseBehavior.Ask)]
    [InlineData(MainWindowCloseBehavior.MinimizeToTray)]
    [InlineData(MainWindowCloseBehavior.Exit)]
    public async Task CloseBehavior_RoundTrip(MainWindowCloseBehavior behavior)
    {
        await _manager.SaveMainWindowCloseBehaviorAsync(behavior);
        (await new SettingsManager(_tempDir).LoadMainWindowCloseBehaviorAsync()).Should().Be(behavior);
    }

    [Fact]
    public async Task CloseBehavior_MissingDefaultsToAsk()
        => (await _manager.LoadMainWindowCloseBehaviorAsync()).Should().Be(MainWindowCloseBehavior.Ask);

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("\"Future\"")]
    [InlineData("42")]
    [InlineData("false")]
    public async Task CloseBehavior_InvalidDefaultsToAskWithoutChangingFile(string json)
    {
        var path = Path.Combine(_tempDir, "close-behavior.json");
        await File.WriteAllTextAsync(path, json);
        (await _manager.LoadMainWindowCloseBehaviorAsync()).Should().Be(MainWindowCloseBehavior.Ask);
        File.ReadAllText(path).Should().Be(json);
    }

    [Theory]
    [InlineData(GameStartFixedTarget.None)]
    [InlineData(GameStartFixedTarget.Lineups)]
    [InlineData(GameStartFixedTarget.Planner)]
    public async Task GameStartSettings_RoundTripThroughNewReader(GameStartFixedTarget fixedTarget)
    {
        var settings = new GameStartSettings { WithLineups = GameStartLineupPage.Pending,
            WithoutLineups = GameStartPlannerPage.Goals, FixedTarget = fixedTarget };
        await _manager.SaveGameStartSettingsAsync(settings);
        (await new SettingsManager(_tempDir).LoadGameStartSettingsAsync()).Should().Be(settings);
        await _manager.SaveGameStartSettingsAsync(settings with { FixedTarget = GameStartFixedTarget.None });
        File.Exists(Path.Combine(_tempDir, "game-start.json.bak")).Should().BeTrue();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"WithLineups\":99}")]
    [InlineData("{\"WithoutLineups\":99}")]
    [InlineData("{\"FixedTarget\":99}")]
    public async Task GameStartSettings_InvalidFileUsesDefaults(string json)
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "game-start.json"), json);
        (await _manager.LoadGameStartSettingsAsync()).Should().Be(new GameStartSettings());
    }

    [Fact]
    public async Task GameStartSettings_FirstLaunchUsesDefaults()
        => (await _manager.LoadGameStartSettingsAsync()).Should().Be(new GameStartSettings());

    [Fact]
    public async Task UiScaleOverrides_RoundTripAndInheritWithoutChangingGlobal()
    {
        var appearance = new SidebarAppearance { UiScale = 1.25, Theme = GameShopTheme.Closure,
            ScaleOverrides = new() { [UiScaleTarget.Tracking] = 0.83, [UiScaleTarget.LineupEditor] = 1.2 } };
        await _manager.SaveSidebarAppearanceAsync(appearance);
        var loaded = await new SettingsManager(_tempDir).LoadSidebarAppearanceAsync();
        loaded.UiScale.Should().Be(1.25);
        loaded.ScaleFor(UiScaleTarget.Tracking, loaded.UiScale).Should().Be(0.83);
        loaded.ScaleFor(UiScaleTarget.Codex, loaded.UiScale).Should().Be(1.25);
        loaded.ScaleFor(UiScaleTarget.OperatorPicker, loaded.ScaleFor(UiScaleTarget.LineupEditor, loaded.UiScale)).Should().Be(1.2);
        loaded.ScaleOverrides.Remove(UiScaleTarget.Tracking);
        loaded.ScaleFor(UiScaleTarget.Tracking, loaded.UiScale).Should().Be(1.25);
        loaded.Theme.Should().Be(GameShopTheme.Closure);
    }

    [Fact]
    public async Task UiScaleOverrides_ValidateFileValuesAtLoadBoundary()
    {
        await _manager.SaveSidebarAppearanceAsync(new SidebarAppearance { UiScale = 1.1,
            ScaleOverrides = new() { [UiScaleTarget.Tracking] = 0, [UiScaleTarget.Lineups] = 8, [(UiScaleTarget)99] = 1 } });
        var loaded = await _manager.LoadSidebarAppearanceAsync();
        loaded.ScaleOverrides.Should().HaveCount(2);
        loaded.ScaleOverrides[UiScaleTarget.Tracking].Should().Be(0.75);
        loaded.ScaleOverrides[UiScaleTarget.Lineups].Should().Be(1.5);
        loaded.UiScale.Should().Be(1.1);
    }

    [Fact]
    public async Task SidebarUiScale_LegacyAppearanceStartsAt100Percent()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "appearance.json"), """{"theme":"Closure","night":{"Closure":true}}""");
        var appearance = await _manager.LoadSidebarAppearanceAsync();
        appearance.UiScale.Should().Be(1);
        appearance.Theme.Should().Be(GameShopTheme.Closure);
        appearance.IsNight(GameShopTheme.Closure).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    public async Task SidebarUiScale_RoundTripsWithoutChangingTheme(double scale)
    {
        await _manager.SaveSidebarAppearanceAsync(new SidebarAppearance { UiScale = scale, Theme = GameShopTheme.Ursus, FollowModeTheme = true });
        var appearance = await new SettingsManager(_tempDir).LoadSidebarAppearanceAsync();
        appearance.UiScale.Should().Be(scale);
        appearance.Theme.Should().Be(GameShopTheme.Ursus);
        appearance.FollowModeTheme.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0.75)]
    [InlineData(9, 1.5)]
    public async Task SidebarUiScale_FileBoundaryClampsOutsideSupportedRange(double scale, double expected)
    {
        await _manager.SaveSidebarAppearanceAsync(new SidebarAppearance { UiScale = scale, Theme = GameShopTheme.Closure });
        var appearance = await _manager.LoadSidebarAppearanceAsync();
        appearance.UiScale.Should().Be(expected);
        appearance.Theme.Should().Be(GameShopTheme.Closure);
    }

    [Fact]
    public async Task DeployPoolRemembersAutomaticFieldRecognitionAfterUpgrade()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "deploy-pool.json"), """{"owned":["角峰"]}""");
        var state = await _manager.LoadDeployPoolAsync();
        Assert.False(state.AutomaticLiveField);
        state.AutomaticLiveField = true;
        await _manager.SaveDeployPoolAsync(state);
        var restored = await new SettingsManager(_tempDir).LoadDeployPoolAsync();
        Assert.True(restored.AutomaticLiveField);
        Assert.Equal(["角峰"], restored.Owned);
    }

    [Theory]
    [InlineData("{\"owned\":")]
    [InlineData("null")]
    [InlineData("{\"owned\":null}")]
    [InlineData("{\"counts\":{\"角峰\":-1}}")]
    public async Task DamagedDeployPoolRestoresValidatedBackupAndPreservesOriginal(string damaged)
    {
        var path = Path.Combine(_tempDir, "deploy-pool.json");
        await _manager.SaveDeployPoolAsync(new() { Owned = ["角峰"], Elite = ["角峰"], Counts = new() { ["角峰"] = 2 } });
        var backup = await File.ReadAllTextAsync(path);
        File.Copy(path, path + ".bak");
        await File.WriteAllTextAsync(path, damaged);

        var recovered = await _manager.LoadDeployPoolAsync();

        recovered.Owned.Should().Equal("角峰");
        recovered.Elite.Should().Equal("角峰");
        recovered.CountOf("角峰").Should().Be(2);
        File.ReadAllText(path).Should().Be(backup);
        File.ReadAllText(path + ".bak").Should().Be(backup);
        File.ReadAllText(Directory.GetFiles(_tempDir, "deploy-pool.corrupt-*.json").Single()).Should().Be(damaged);
        _manager.DeployPoolRecoveryNotice.Should().Contain("已恢复");
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
        (await new SettingsManager(_tempDir).LoadDeployPoolAsync()).Owned.Should().Equal("角峰");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{\"owned\":null}")]
    [InlineData("truncated")]
    public async Task DamagedDeployPoolWithoutValidBackupStopsLoadingWithoutOverwriting(string? backup)
    {
        var path = Path.Combine(_tempDir, "deploy-pool.json");
        await File.WriteAllTextAsync(path, "broken");
        if (backup is not null) await File.WriteAllTextAsync(path + ".bak", backup);

        Func<Task> load = () => _manager.LoadDeployPoolAsync();
        await load.Should().ThrowAsync<InvalidDataException>().WithMessage("*没有可用备份*");

        File.ReadAllText(path).Should().Be("broken");
        if (backup is not null) File.ReadAllText(path + ".bak").Should().Be(backup);
        File.ReadAllText(Directory.GetFiles(_tempDir, "deploy-pool.corrupt-*.json").Single()).Should().Be("broken");
    }

    [Fact]
    public async Task ValidEmptyDeployPoolDoesNotResurrectAnOlderBackup()
    {
        var path = Path.Combine(_tempDir, "deploy-pool.json");
        (await _manager.LoadDeployPoolAsync()).Owned.Should().BeEmpty();
        await _manager.SaveDeployPoolAsync(new() { Owned = ["角峰"] });
        await _manager.SaveDeployPoolAsync(new());
        (await _manager.LoadDeployPoolAsync()).Owned.Should().BeEmpty();
        _manager.DeployPoolRecoveryNotice.Should().BeNull();
        Directory.GetFiles(_tempDir, "deploy-pool.corrupt-*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task LockedPreferenceKeepsOriginalAndReleasesSaveLockAfterFailure()
    {
        await _manager.SaveAutomaticPurchaseTrackingAsync(false);
        var path = Path.Combine(_tempDir, "automatic-purchases.json");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Func<Task> save = () => _manager.SaveAutomaticPurchaseTrackingAsync(true);
            var error = await Record.ExceptionAsync(save);
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file access failure, got {error}");
            (await _manager.LoadAutomaticPurchaseTrackingAsync()).Should().BeFalse();
        }
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
        await _manager.SaveAutomaticPurchaseTrackingAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
        (await _manager.LoadAutomaticPurchaseTrackingAsync()).Should().BeTrue();
        File.ReadAllText(path + ".bak").Should().Be("false");
    }

    [Fact]
    public async Task ConcurrentPreferenceSavesKeepLatestSnapshotAndValidBackup()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _manager.SaveSkippedVersionAsync($"v1.0.{i}")));
        (await _manager.LoadSkippedVersionAsync()).Should().Be("v1.0.19");
        File.ReadAllText(Path.Combine(_tempDir, "skipped_version.txt.bak")).Should().Be("v1.0.18");
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task AutomaticPurchasesDefaultOffAndRoundTripWithoutChangingTracking()
    {
        await _manager.SaveTrackingEntriesAsync([new() { Name = "角峰", Type = TrackingType.Operator }]);
        (await _manager.LoadAutomaticPurchaseTrackingAsync()).Should().BeFalse();
        await _manager.SaveAutomaticPurchaseTrackingAsync(true);
        (await new SettingsManager(_tempDir).LoadAutomaticPurchaseTrackingAsync()).Should().BeTrue();
        await _manager.SaveAutomaticPurchaseTrackingAsync(false);
        (await new SettingsManager(_tempDir).LoadAutomaticPurchaseTrackingAsync()).Should().BeFalse();
        (await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules)).Should().ContainSingle().Which.Name.Should().Be("角峰");
    }

    [Fact]
    public async Task AutomaticGameThemeDefaultsToManualAndRoundTripsIndependently()
    {
        await _manager.SaveGameShopThemeAsync(GameShopTheme.Ursus);
        (await _manager.LoadAutomaticGameThemeAsync()).Should().BeFalse();
        await _manager.SaveAutomaticGameThemeAsync(true);
        (await _manager.LoadAutomaticGameThemeAsync()).Should().BeTrue();
        (await _manager.LoadGameShopThemeAsync()).Should().Be(GameShopTheme.Ursus);
        await _manager.SaveAutomaticGameThemeAsync(false);
        (await _manager.LoadAutomaticGameThemeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RapidTrackingSavesAreSerializedAndLeaveCompleteLatestJson()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _manager.SaveTrackingEntriesAsync(
            [new() { Name = $"目标{i}", Type = TrackingType.Operator }])));
        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        loaded.Should().ContainSingle().Which.Name.Should().Be("目标19");
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
    }

    private readonly string _tempDir;
    private readonly SettingsManager _manager;

    public SettingsManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"asp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new SettingsManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task SaveAndLoad_TrackingEntries_RoundTrips()
    {
        var entries = new List<TrackingEntry>
        {
            new() { Name = "角峰", Type = TrackingType.Operator, TransformedCovenant = "炎" },
            new() { Name = "不屈弹射器", Type = TrackingType.Equipment }
        };

        await _manager.SaveTrackingEntriesAsync(entries);
        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);

        loaded.Should().HaveCount(4);
        loaded.Should().Contain(e => e.Name == "角峰" && e.Type == TrackingType.Operator);
        loaded.Should().Contain(e => e.Name == "不屈弹射器" && e.Type == TrackingType.Equipment);
        loaded.Single(e => e.Name == "角峰").Equipments.Should().Equal("变形同构体", "炎国短刀");
        loaded.Single(e => e.Name == "角峰").TransformedCovenant.Should().BeNull();
    }

    [Fact]
    public async Task OverlappingTrackingSaves_KeepTheNewestList()
    {
        var saves = Enumerable.Range(1, 20).Select(n => _manager.SaveTrackingEntriesAsync(
            Enumerable.Range(0, n).Select(i => new TrackingEntry
            {
                Name = $"干员{i}", Type = TrackingType.Operator,
                Role = i % 2 == 0 ? TrackingRole.Final : TrackingRole.Transition, Stages = i % 2 == 0 ? "" : "开局"
            }).ToList())).ToList();
        await Task.WhenAll(saves);
        // The last caller may return while an earlier caller is still writing its snapshot.
        for (var i = 0; i < 50 && (await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules)).Count != 20; i++) await Task.Delay(20);

        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        loaded.Should().HaveCount(20);
        loaded[1].Role.Should().Be(TrackingRole.Transition);
        loaded[1].Stages.Should().Be("开局");
    }

    [Fact]
    public async Task OldTrackingEntriesDefaultToNoPlannedTransformation()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tracking.json"), """[{"name":"角峰","type":0}]""");
        (await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules)).Should().ContainSingle().Which.TransformedCovenant.Should().BeNull();
    }

    [Fact]
    public async Task LoadTrackingEntries_NoFile_ReturnsEmpty()
    {
        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        loaded.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAndLoad_LightTheme_RoundTrips(bool isLight)
    {
        await _manager.SaveLightThemeAsync(isLight);
        (await _manager.LoadLightThemeAsync()).Should().Be(isLight);
    }

    [Fact]
    public async Task LoadLightTheme_NoFile_UsesDarkTheme()
    {
        (await _manager.LoadLightThemeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task NewPreferences_RequireThemeChoiceAndUseSingleLineup()
    {
        (await _manager.LoadGameShopThemeAsync()).Should().BeNull();
        (await _manager.LoadTrackMultipleLineupsAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(GameShopTheme.Terminal)]
    [InlineData(GameShopTheme.Ursus)]
    [InlineData(GameShopTheme.Closure)]
    public async Task GameShopTheme_PersistsAcrossManagerInstances(GameShopTheme theme)
    {
        await _manager.SaveGameShopThemeAsync(theme);
        (await new SettingsManager(_tempDir).LoadGameShopThemeAsync()).Should().Be(theme);
        (await File.ReadAllTextAsync(Path.Combine(_tempDir, "game-theme.json")))
            .Should().Be($"\"{theme}\"");
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("\"UnknownTheme\"")]
    [InlineData("99")]
    public async Task InvalidThemePreference_RequiresChoice(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "game-theme.json"), json);
        (await _manager.LoadGameShopThemeAsync()).Should().BeNull();
    }

    [Fact]
    public async Task MissingSettingsDirectory_RequiresChoice()
    {
        var manager = new SettingsManager(Path.Combine(_tempDir, "first-start"));
        (await manager.LoadGameShopThemeAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleLineups_PersistsAcrossManagerInstances(bool enabled)
    {
        await _manager.SaveTrackMultipleLineupsAsync(enabled);
        (await new SettingsManager(_tempDir).LoadTrackMultipleLineupsAsync()).Should().Be(enabled);
    }

    [Fact]
    public async Task SidePanelPosition_FirstStartUsesDefaultAttachment()
    {
        var settings = await new SettingsManager(Path.Combine(_tempDir, "first-start")).LoadSidePanelPositionAsync();
        // Free movement by default; without a placement the panel docks beside the game once.
        settings.FreeMovement.Should().BeTrue();
        settings.ForceTopmost.Should().BeFalse();
        settings.Placement.Should().BeNull();
        settings.FollowOffsetDip.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SidePanelPosition_PreservesModePlacementAndFollowOffset(bool free)
    {
        var settings = new SidePanelPositionSettings
        {
            FreeMovement = free,
            ForceTopmost = !free,
            Placement = new SidePanelPlacement(-1280, 150, 310, 600),
            FollowOffsetDip = new SidePanelOffset(-900.5, 80.25),
        };
        await _manager.SaveSidePanelPositionAsync(settings);
        (await new SettingsManager(_tempDir).LoadSidePanelPositionAsync()).Should().Be(settings);
        File.Exists(Path.Combine(_tempDir, "side-panel-position.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public async Task SidePanelPosition_OverlappingSavesKeepLastCompleteState()
    {
        var saves = Enumerable.Range(0, 8).Select(i => _manager.SaveSidePanelPositionAsync(new SidePanelPositionSettings
        {
            FreeMovement = i % 2 == 1, ForceTopmost = i % 2 == 1, FollowOffsetDip = new SidePanelOffset(i, i * 2),
        })).ToArray();
        await Task.WhenAll(saves);
        var saved = await new SettingsManager(_tempDir).LoadSidePanelPositionAsync();
        saved.FreeMovement.Should().BeTrue();
        saved.ForceTopmost.Should().BeTrue();
        saved.FollowOffsetDip.Should().Be(new SidePanelOffset(7, 14));
    }

    [Fact]
    public async Task SidePanelPosition_OldFileKeepsPlacementAndDefaultsForceTopmostOff()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "side-panel-position.json"),
            "{\"freeMovement\":false,\"placement\":{\"left\":-1280,\"top\":150,\"widthDip\":310,\"heightDip\":600}}");
        var settings = await _manager.LoadSidePanelPositionAsync();
        settings.ForceTopmost.Should().BeFalse();
        settings.FreeMovement.Should().BeFalse();
        settings.Placement.Should().Be(new SidePanelPlacement(-1280, 150, 310, 600));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid JSON")]
    [InlineData("{\"Placement\":{\"Left\":0,\"Top\":0,\"WidthDip\":0,\"HeightDip\":600}}")]
    [InlineData("{\"Placement\":{\"Left\":1e300,\"Top\":0,\"WidthDip\":310,\"HeightDip\":600}}")]
    [InlineData("{\"Placement\":{\"Left\":0,\"Top\":0,\"WidthDip\":310,\"HeightDip\":-1}}")]
    public async Task SidePanelPosition_RejectsMalformedOrInvalidCoordinates(string json)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "side-panel-position.json"), json);
        Func<Task> load = () => _manager.LoadSidePanelPositionAsync();
        await load.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task TerminalLevel_RoundTripsAndExpires()
    {
        var t0 = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        (await _manager.LoadTerminalLevelAsync(t0)).Should().BeNull();
        await _manager.SaveTerminalLevelAsync(4, t0);
        (await _manager.LoadTerminalLevelAsync(t0.AddMinutes(30))).Should().Be(4);
        (await _manager.LoadTerminalLevelAsync(t0.AddHours(3))).Should().BeNull("a level from hours ago belongs to an old run");
        await _manager.SaveTerminalLevelAsync(0, t0);
        (await _manager.LoadTerminalLevelAsync(t0)).Should().BeNull();
    }

    [Fact]
    public async Task AcquiredTrackingRowsRoundTripWithoutReactivatingEquipment()
    {
        await _manager.SaveTrackingEntriesAsync([
            new() { Name = "角峰", Type = TrackingType.Operator, IsAcquired = true, Equipments = ["不屈弹射器"] },
            new() { Name = "不屈弹射器", Type = TrackingType.Equipment, IsAcquired = true, IsManual = false }
        ]);
        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        var vm = new ASPAssistant.Core.ViewModels.TrackingViewModel();
        vm.LoadEntries(loaded);
        vm.AllEntries.Should().HaveCount(2).And.OnlyContain(e => e.IsAcquired);
        vm.IsTracked("角峰").Should().BeFalse();
        vm.IsTracked("不屈弹射器").Should().BeFalse();
        vm.ToggleAcquired("角峰");
        vm.ToggleAcquired("不屈弹射器");
        await _manager.SaveTrackingEntriesAsync(vm.AllEntries.ToList());
        var resumed = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        resumed.Should().HaveCount(2).And.OnlyContain(e => !e.IsAcquired);
    }

    [Fact]
    public async Task TrackingJsonWithoutAcquiredFlagRemainsActive()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tracking.json"),
            """[{"name":"角峰","type":0,"equipments":[]}]""");
        var loaded = await _manager.LoadTrackingEntriesAsync(EquipmentTestData.Rules);
        loaded.Should().ContainSingle().Which.IsAcquired.Should().BeFalse();
    }
}
