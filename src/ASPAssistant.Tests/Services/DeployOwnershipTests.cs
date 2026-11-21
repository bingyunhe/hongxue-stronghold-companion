using System.Diagnostics;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using ASPAssistant.Core.ViewModels;

namespace ASPAssistant.Tests.Services;

public class DeployOwnershipTests
{
    private readonly Guid _game = Guid.NewGuid();

    private static LiveUnit Unit(long id, string name, int col, int row = 1) =>
        new(new(new(col, row), 1, default, [], name) { StandaloneName = name }, id, name, null, false, false);

    private FieldOwnership Board(params LiveUnit[] units) => new(
        new(_game, new(DateTimeOffset.UtcNow, "实况原帧", []), "city", 8, units, []) { PersonalBoard = true }, units);

    [Fact]
    public void WeiLatePurchaseMovementAndSimultaneousCopies()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var bench = Unit(12, "维伊", 6, -1);
        var frame = Board(bench);
        Assert.True(sync.Observe(frame));
        sync.Purchase("维伊", frame.Frame.Frame.Time.AddSeconds(-2), 8);
        Assert.Equal(1, pool.CountOf("维伊"));
        Assert.False(sync.Observe(frame));
        var field = Unit(12, "维伊", 3);
        Assert.False(sync.Observe(Board(field)));
        Assert.False(sync.Observe(Board()));
        sync.Purchase("维伊", DateTimeOffset.UtcNow, 9);
        Assert.Equal(1, pool.CountOf("维伊"));
        Assert.True(sync.Observe(Board(field, bench with { Id = 13 })));
        Assert.Equal(2, pool.CountOf("维伊"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CorrectingJackRetractsOnlyTheCopyAddedByThatObservation(bool separatelyOwned, bool wrongFirst)
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var wrong = Unit(11, "杰克", 5);
        var real = Unit(10, "杰克", 3, -1);
        if (separatelyOwned) sync.Purchase("杰克", DateTimeOffset.UtcNow, 8);
        var both = !separatelyOwned ? Board(wrong) : wrongFirst ? Board(wrong, real) : Board(real, wrong);
        sync.Observe(both);
        var correct = wrong with { Name = "卡达", Final = true, Manual = true };
        var resolved = (separatelyOwned ? Board(real, correct) : Board(correct)) with { ConfirmedTrack = 11, PreviousName = "杰克" };
        Assert.True(sync.Observe(resolved));
        Assert.Equal(separatelyOwned ? 1 : 0, pool.CountOf("杰克"));
        Assert.Equal(1, pool.CountOf("卡达"));
        Assert.False(sync.Observe(both));
        Assert.False(sync.Observe(resolved));
    }

    [Fact]
    public void ANewTrackWithTheSameGuessCannotEraseAnOlderUnseenCopy()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        sync.Observe(Board(Unit(1, "杰克", 2)));
        var guessed = Unit(2, "杰克", 4);
        sync.Observe(Board(guessed));
        sync.Observe(Board(guessed with { Name = "卡达", Final = true }) with { ConfirmedTrack = 2, PreviousName = "杰克" });
        Assert.Equal(1, pool.CountOf("杰克"));
        Assert.Equal(1, pool.CountOf("卡达"));
    }

    [Fact]
    public void PeerUnknownAndReviewCandidatesHaveNoEffect()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var unit = Unit(1, "烈夏", 4);
        var frame = Board(unit);
        Assert.False(sync.Observe(frame with { Frame = frame.Frame with { PersonalBoard = false }, ConfirmedTrack = 1 }));
        Assert.False(sync.Observe(Board(unit with { Name = null, Suggestion = new(3, "烈夏", new(4, 1)) })));
        Assert.False(sync.Observe(Board(unit with { NeedsReview = true })));
        Assert.False(sync.Observe(Board(unit with { Mark = unit.Mark with { StandaloneName = null } })));
        Assert.Empty(pool.Owned);
    }

    [Fact]
    public async Task ManualRemovalSurvivesOldEvidenceRestartAndNewRun()
    {
        var settings = new SettingsManager(Path.Combine(Path.GetTempPath(), $"ownership-{Guid.NewGuid():N}"));
        var pool = new DeployPoolState { Extra = ["地灵"] };
        var sync = new DeployOwnership(pool);
        var unit = Unit(4, "灰喉", 3);
        var frame = Board(unit);
        sync.Observe(frame);
        sync.SetManualCount("灰喉", 0);
        sync.Observe(Board(Unit(9, "杰克", 5)));
        await settings.SaveDeployPoolAsync(pool);
        var restored = await settings.LoadDeployPoolAsync();
        sync = new(restored);
        Assert.False(sync.Observe(frame));
        Assert.False(sync.Purchase("灰喉", frame.Frame.Frame.Time, 11));
        Assert.False(restored.IsOwned("灰喉"));
        var boughtAgain = restored.ManualLimits["灰喉"].ChangedAt.AddSeconds(1);
        Assert.True(sync.Purchase("灰喉", boughtAgain, 12));
        Assert.Equal(1, restored.CountOf("灰喉"));
        var differentGame = Board(Unit(9, "卡达", 5));
        sync.Observe(differentGame with { Frame = differentGame.Frame with { Game = Guid.NewGuid() }, ConfirmedTrack = 9, PreviousName = "杰克" });
        Assert.Equal(1, restored.CountOf("杰克"));
        sync.SetManualCount("灰喉", 0);
        var tracking = new TrackingViewModel();
        tracking.SetDeployPool(restored);
        tracking.StartNewRun();
        Assert.Empty(restored.ManualLimits);
        Assert.Empty(restored.FieldCopies);
        Assert.Empty(restored.Owned);
        Assert.Equal(["地灵"], restored.Extra);
    }

    [Fact]
    public void ExplicitSaleLimitsCopiesUntilConfirmedAgain()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var frame = Board(Unit(1, "烈夏", 4), Unit(2, "烈夏", 7, -1));
        sync.Observe(frame);
        Assert.True(sync.Observe(frame with { Sold = "烈夏" }));
        Assert.False(sync.Observe(frame));
        Assert.Equal(1, pool.CountOf("烈夏"));
        Assert.True(sync.Observe(frame with { ConfirmedTrack = 2 }));
        Assert.Equal(2, pool.CountOf("烈夏"));
    }

    [Fact]
    public void SellingOneFieldCopyDoesNotMakeItsMisidentifiedTwinPermanent()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var first = Unit(2, "杰克", 4);
        var second = Unit(3, "杰克", 7, -1);
        sync.Observe(Board(first, second));
        sync.Observe(Board(second) with { Sold = "杰克", ConfirmedTrack = first.Id });
        sync.Observe(Board(second with { Name = "卡达", Final = true }) with { ConfirmedTrack = second.Id, PreviousName = "杰克" });
        Assert.Equal(0, pool.CountOf("杰克"));
        Assert.Equal(1, pool.CountOf("卡达"));
    }

    [Fact]
    public void RepeatedTwelveUnitBoardHasNoStateWrites()
    {
        var pool = new DeployPoolState();
        var sync = new DeployOwnership(pool);
        var frame = Board(Enumerable.Range(0, 12).Select(i => Unit(i, $"干员{i}", i % 6, i < 6 ? 1 : -1)).ToArray());
        sync.Observe(frame);
        var times = new double[1000];
        for (int i = 0; i < times.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            bool changed = sync.Observe(frame);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.False(changed);
        }
        Array.Sort(times);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "ownership-timing.txt"),
            $"12 units x {times.Length}, p50={times[500]:F4} ms, p95={times[950]:F4} ms, changes=0");
    }
}
