using System.Drawing;
using System.Drawing.Imaging;
using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class ShopPurchaseDetectorTests
{
    private static ShopItem Card(string name, double x) => new() { Name = name, Id = name + "@0", OcrRegion = (x, .94, .06, .025) };
    private static ShopEconomy Funds(int money, int round = 1) => new(round, money, 5, false, "规程等级7", true);

    private static byte[] Frame(bool first = true, bool second = true)
    {
        using var bitmap = new Bitmap(960, 540);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(15, 25, 40));
        if (first) g.FillRectangle(Brushes.LightGray, 300, 420, 90, 120);
        if (second) g.FillRectangle(Brushes.LightGray, 420, 420, 90, 120);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstCardSelectionRequiresItsPurchaseButtonToCompleteTheBaseline(bool buttonVisible)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("地灵", .33), Card("深靛", .46)];
        using var image = new Bitmap(960, 540);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.DarkGray);
            if (buttonVisible) g.FillRectangle(Brushes.Cyan, 315, 462, 32, 58);
        }
        var selected = ImageBytes.Encode(image);
        var targets = new HashSet<string> { "地灵" };
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(selected, [cards[1]], true, Funds(4), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().BeEmpty();
        var bought = detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets);
        if (buttonVisible) bought.Should().Equal("地灵");
        else bought.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoIdenticalPurchasesRetainTheirSlotsAcrossABriefFold(bool fold)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("地灵", .33), Card("地灵", .46), Card("野鬃", .65)];
        var targets = cards.Select(c => c.Name).ToHashSet();
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        var paid = Frame(false, false);
        detector.Observe(paid, [cards[2]], true, Funds(1), targets).Should().BeEmpty();
        if (fold) detector.Observe(paid, [], false, Funds(1), targets).Should().BeEmpty();
        detector.Observe(paid, [cards[2]], true, Funds(1), targets).Should().Equal("地灵", "地灵");
        detector.Observe(paid, [cards[2]], true, Funds(1), targets).Should().BeEmpty();
    }

    [Theory]
    [InlineData("same")]
    [InlineData("refresh")]
    [InlineData("round")]
    [InlineData("all-empty")]
    public void PendingGravelPurchaseRequiresTheSameRemainingOffersAfterTheShopReopens(string scene)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] offers = [Card("砾", .33), Card("霜叶", .46)];
        var targets = offers.Select(c => c.Name).ToHashSet();
        detector.Observe(Frame(), offers, true, Funds(10, 5), targets);
        detector.Observe(Frame(), offers, true, Funds(10, 5), targets);
        var paid = Frame(false);
        detector.Observe(paid, [offers[1]], true, Funds(7, 5), targets).Should().BeEmpty();
        detector.Observe(paid, [], false, Funds(7, 5), targets).Should().BeEmpty();
        ShopItem[] remaining = scene switch { "all-empty" => [], "refresh" => [Card("锡人", .46)], _ => [offers[1]] };
        var result = detector.Observe(paid, remaining, true, Funds(7, scene == "round" ? 6 : 5), targets);
        if (scene == "same") result.Should().Equal("砾");
        else result.Should().BeEmpty();
    }

    [Fact]
    public void BlankPaidRefreshAfterAnEnemyPanelCannotConfirmTheOldOffers()
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("地灵", .33), Card("深靛", .46)];
        var targets = cards.Select(c => c.Name).ToHashSet();
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.PauseForEnemyPanel();
        detector.Observe(Frame(false, false), [], true, Funds(3), targets).Should().BeEmpty();
        detector.Observe(Frame(false, false), [], true, Funds(3), targets).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptySlotDecorationIsNotTreatedAsACardBorder(int nameJitter)
    {
        using var stream = new MemoryStream(Frame(false));
        using var bitmap = new Bitmap(stream);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.FromArgb(40, 70, 120)))
            graphics.FillRectangle(brush, 320, 485, 16, 6);
        ShopPurchaseDetector.IsEmpty(bitmap, Card("随身身份牌", (316 + nameJitter) / 960.0)).Should().BeTrue();
    }

    [Fact]
    public void DarkCardWithAFullWidthBottomBorderIsNotAnEmptySlot()
    {
        using var stream = new MemoryStream(Frame(false));
        using var bitmap = new Bitmap(stream);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.FromArgb(40, 70, 120)))
            graphics.FillRectangle(brush, 300, 532, 90, 3);
        ShopPurchaseDetector.IsEmpty(bitmap, Card("冻结装备", .33)).Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void PurchaseNeedsStableCardsTwoEmptyFramesAndFundsDecrease(int price)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "角峰" };
        detector.Observe(Frame(), cards, true, Funds(20), targets).Should().BeEmpty();
        detector.NeedsFundsRead(cards, true).Should().BeTrue("the baseline needs a second independent balance read");
        detector.Observe(Frame(), cards, true, Funds(20), targets).Should().BeEmpty();
        detector.NeedsFundsRead(cards, true).Should().BeFalse("a confirmed baseline can be reused while cards stay unchanged");
        detector.NeedsFundsRead([cards[1]], true).Should().BeTrue();
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().Equal("角峰");
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("closed")]
    [InlineData("round")]
    [InlineData("refresh")]
    [InlineData("money")]
    [InlineData("reset")]
    public void OtherTransitionsCannotMarkAnOperatorAcquired(string transition)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "角峰" };
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        if (transition == "reset") detector.Reset("test-reset");
        var image = Frame(transition == "ocr");
        var next = transition == "refresh" ? new[] { Card("拜松", .33), cards[1] } : [cards[1]];
        var funds = transition == "round" ? Funds(19, 2) : Funds(transition == "money" ? 20 : 19);
        detector.Observe(image, next, transition != "closed", funds, targets).Should().BeEmpty();
        detector.Observe(image, next, transition != "closed", funds, targets).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(6, 0)]
    [InlineData(6, 1)]
    public void FundsCanDecreaseBeforeThePurchasedCardDisappears(int price, int unchangedScans)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "角峰" };
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(), cards, true, Funds(20 - price), targets).Should().BeEmpty();
        for (var i = 0; i < unchangedScans; i++)
            detector.Observe(Frame(), cards, true, Funds(20 - price), targets).Should().BeEmpty();
        detector.NeedsFundsRead(cards, true).Should().BeFalse();
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().Equal("角峰");
        detector.Observe(Frame(false), [cards[1]], true, Funds(20 - price), targets).Should().BeEmpty();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("refresh")]
    [InlineData("closed")]
    [InlineData("round")]
    [InlineData("reset")]
    public void EarlierFundsDecreaseCannotSurviveUnrelatedChanges(string change)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "角峰" };
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        if (change == "reset") detector.Reset("test-reset");
        if (change == "closed") detector.Observe(Frame(), cards, false, Funds(4), targets);
        if (change == "round") detector.Observe(Frame(), cards, true, Funds(4, 2), targets);
        if (change == "refresh") detector.Observe(Frame(), [Card("新卡", .33), cards[1]], true, Funds(4), targets);
        for (var i = 0; i < (change == "expired" ? 2 : 1); i++)
            detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(4), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(4), targets).Should().BeEmpty();
    }

    [Fact]
    public void UntrackedPurchaseConsumesTheEarlierFundsDecrease()
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("未追踪装备", .33), Card("角峰", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "角峰" };
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        detector.Observe(Frame(), cards, true, Funds(5), targets);
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(4), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(4), targets);
        detector.Observe(Frame(false, false), [], true, Funds(4), targets).Should().BeEmpty();
        detector.Observe(Frame(false, false), [], true, Funds(4), targets).Should().BeEmpty();
    }

    [Fact]
    public void SameNamedCardsUsePositionsAndOnlyTrackedEquipmentIsEligible()
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("源石溶剂", .33), Card("源石溶剂", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { "源石溶剂" };
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(19), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(19), targets).Should().Equal("源石溶剂");
        detector.Observe(Frame(false, false), [], true, Funds(18), new HashSet<string>());
        detector.Observe(Frame(false, false), [], true, Funds(18), new HashSet<string>()).Should().BeEmpty();
    }

    [Fact]
    public void BalanceMisreadDoesNotReplaceTheConfirmedBaselineOrCountAsFreshEvidence()
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        var targets = new HashSet<string> { cards[0].Name };
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(), cards, true, Funds(20), targets);
        detector.Observe(Frame(), cards, true, Funds(20), targets, freshFunds: false);
        detector.Observe(Frame(false), [cards[1]], true, Funds(4), targets).Should().BeEmpty();
        detector.NeedsFundsRead([cards[1]], true).Should().BeTrue();
        detector.Observe(Frame(false), [cards[1]], true, Funds(20), targets).Should().BeEmpty();
        detector.NeedsFundsRead([cards[1]], true).Should().BeTrue();
        detector.Observe(Frame(false), [cards[1]], true, Funds(2), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(2), targets, freshFunds: false).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(2), targets).Should().Equal("角峰");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(null)]
    public void EquipmentPurchaseSurvivesUnreliableFundsUntilTwoValidEmptyFrames(int? badMoney)
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("虹彩活血饮料", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { cards[0].Name };
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(2) with { Money = badMoney }, targets).Should().BeEmpty();
        detector.NeedsFundsRead([cards[1]], true).Should().BeTrue("a pending purchase gets a short burst of balance-only reads");
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().Equal("虹彩活血饮料");
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().BeEmpty();
    }

    [Fact]
    public void PersistentUnreadableFundsReturnToPeriodicReadsAfterFourAttempts()
    {
        var detector = new ShopPurchaseDetector();
        ShopItem[] cards = [Card("角峰", .33), Card("霜叶", .46)];
        var targets = new HashSet<string> { cards[0].Name };
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        detector.Observe(Frame(), cards, true, Funds(4), targets);
        for (int i = 0; i < 4; i++)
        {
            detector.NeedsFundsRead([cards[1]], true).Should().BeTrue();
            detector.Observe(Frame(false), [cards[1]], true, Funds(20), targets).Should().BeEmpty();
        }
        detector.NeedsFundsRead([cards[1]], true).Should().BeFalse();
        detector.Observe(Frame(false), [cards[1]], true, Funds(2), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(2), targets).Should().Equal("角峰");
    }

    [Fact]
    public void WaitingForFundsStillRejectsARefreshAndDoesNotForgetThePurchaseDuringAnAnimation()
    {
        ShopItem[] cards = [Card("虹彩活血饮料", .33), Card("霜叶", .46)];
        IReadOnlySet<string> targets = new HashSet<string> { cards[0].Name };
        var detector = new ShopPurchaseDetector();
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(), [cards[1]], true, Funds(1), targets).Should().BeEmpty("the card animation still covers the empty slot");
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().BeEmpty();
        detector.Observe(Frame(false), [cards[1]], true, Funds(1), targets).Should().Equal("虹彩活血饮料");
        detector.Reset("test-reset");
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(), cards, true, Funds(2), targets);
        detector.Observe(Frame(false), [cards[1]], true, Funds(16), targets);
        ShopItem[] refreshed = [Card("新卡", .33), cards[1]];
        detector.Observe(Frame(), refreshed, true, Funds(1), targets).Should().BeEmpty();
        detector.Observe(Frame(), refreshed, true, Funds(1), targets).Should().BeEmpty();
    }
}
