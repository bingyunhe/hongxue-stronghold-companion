// Added for ASP Assistant Arknights on 2026-11-13; see NOTICE.
using System.Drawing;
using System.Drawing.Drawing2D;
using ASPAssistant.Core.Services;
using FluentAssertions;
using Moq;

namespace ASPAssistant.Tests.Services;

/// <summary>Real bundled OCR on shop frames: round and mode at the top, funds and upgrade price at the bottom.</summary>
[Collection("Native OCR")]
public class ShopEconomyReaderTests(ShopReaderFixture fixture) : IClassFixture<ShopReaderFixture>
{
    [Trait("Category", "LocalScreenshot")]
    [Theory]
    [InlineData(1449, 815)]
    [InlineData(1280, 720)]
    public async Task MultiplayerFirstRoundReadsTheVisibleUpgradePrice(int width, int height)
    {
        using var original = new Bitmap(fixture.Sample("shop-round1-multiplayer-1449x815.png"));
        using var image = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(original, 0, 0, width, height);
        }
        var bytes = ImageBytes.Encode(image);
        var actual = await new ShopEconomyReader(fixture.Engine).ReadAsync(bytes, width, height);
        actual.Should().Be(new ShopEconomy(1, 4, 5, false, "险境模拟", true));
    }

    [Trait("Category", "LocalScreenshot")]
    [Fact]
    public async Task RecentLocalShopReadsTheConfirmedDifficultyAndVisibleEconomy()
    {
        var bytes = File.ReadAllBytes(fixture.Sample("regulation-level-shop-1449x815.png"));
        var (width, height) = ImageBytes.GetDimensions(bytes);
        var read = await new ShopEconomyReader(fixture.Engine).ReadAsync(bytes, width, height);
        read.Should().Be(new ShopEconomy(1, 4, 5, false, "规程等级7", true));
    }

    [Theory]
    [InlineData("shop-terminal-ui100-2000x1125.png", 2000, 1125)]
    [InlineData("shop-ursus-ui100-2000x1125.png", 2000, 1125)]
    [InlineData("shop-ui100-2000x1125.png", 2000, 1125)]
    [InlineData("shop-closure-round1-1600x1024.png", 1600, 1024)]
    public async Task ReadsFullFramesInEveryTheme(string file, int width, int height)
    {
        var read = await new ShopEconomyReader(fixture.Engine).ReadAsync(File.ReadAllBytes(fixture.Sample(file)), width, height);
        read.Should().Be(new ShopEconomy(1, 4, 5, false, "规程等级1", true));
    }

    [Theory]
    [InlineData("shop-terminal-tier1-color.png", 4, 5, false)]
    [InlineData("shop-terminal-tier2-duplicates.png", 2, 8, false)]
    [InlineData("shop-terminal-tier4-gray.png", 0, 12, false)]
    [InlineData("shop-terminal-tier4-frozen-gray.png", 0, 12, false)]
    [InlineData("shop-frontier-tier1-color.png", 4, 5, false)]
    [InlineData("shop-frontier-tier2-glow.png", 6, 7, false)]
    [InlineData("shop-frontier-tier2-frozen-gray.png", 0, 7, false)]
    [InlineData("shop-frontier-tier4-gray-glow.png", 0, 12, false)]
    [InlineData("shop-tier2-color.png", null, 6, false)]
    [InlineData("shop-tier4-gray.png", null, 12, false)]
    [InlineData("shop-tier6-frozen-gray-glow.png", 0, null, true)]
    [InlineData("shop-tier6-glow-a.png", 11, null, true)]
    [InlineData("shop-tier6-glow-b.png", 11, null, true)]
    public async Task ReadsFundsAndPrice_AndNeverGuessesTheMaskedTop(string file, int? money, int? cost, bool maxed)
    {
        var read = await new ShopEconomyReader(fixture.Engine).ReadAsync(File.ReadAllBytes(fixture.Sample(file)), 1920, 1080);
        read.Should().Be(new ShopEconomy(null, money, cost, maxed, null, false));
    }

    [Theory]
    [InlineData("休息一下", true)]
    [InlineData("作战中", false)]
    public async Task ReadsPhaseBadgeWhenCoopHudOmitsIt(string phase, bool preparing)
    {
        var image = Array.Empty<byte>();
        var ocr = new Mock<IOcrEngine>(MockBehavior.Strict);
        ocr.Setup(o => o.RecognizeRegionAsync(image, 0, 0, 768, 64)).ReturnsAsync(
            new OcrTextResult[] { new("终极模拟", default), new("回合2", default) });
        ocr.Setup(o => o.RecognizeLineAsync(image, 622, 18, 81, 24)).ReturnsAsync(
            new OcrTextResult[] { new(phase, default) });
        var result = await new ShopEconomyReader(ocr.Object).ReadPreparationAsync(image, 1280, 720);
        Assert.Equal(2, result.Round);
        Assert.Equal(preparing, result.IsPreparation);
    }

    [Fact]
    public async Task BanScreenHasNoShopHud()
    {
        var read = await new ShopEconomyReader(fixture.Engine).ReadAsync(File.ReadAllBytes(fixture.Sample("ban-sample-1920x1080.png")), 1920, 1080);
        read.UpgradeCost.Should().BeNull();
        read.UpgradeMaxed.Should().BeFalse();
    }
}

public class ShopEconomyNumberTests
{
    [Theory]
    [InlineData("🎆终极模拟", "终极模拟")]
    [InlineData("◆ 险境模拟", "险境模拟")]
    [InlineData("终极模拟", "终极模拟")]
    [InlineData("★规程等级7", "规程等级7")]
    [InlineData("说明终极模拟", null)]
    [InlineData("A终极模拟", null)]
    [InlineData("1终极模拟", null)]
    [InlineData("终极模拟说明", null)]
    public async Task ModeAllowsLeadingIconButRejectsOtherWords(string text, string? expected)
    {
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(e => e.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        ocr.Setup(e => e.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        ocr.Setup(e => e.RecognizeRegionAsync(It.IsAny<byte[]>(), 0, 0, 1152, 97))
            .ReturnsAsync([new(text, (150, 20, 200, 40)), new("回合??", (600, 20, 100, 40))]);
        var read = await new ShopEconomyReader(ocr.Object).ReadAsync([], 1920, 1080);
        read.Mode.Should().Be(expected);
        read.Round.Should().BeNull();
    }

    [Theory]
    [InlineData("⑤", 5)]
    [InlineData("⓪", 0)]
    [InlineData("⑩", 10)]
    [InlineData("１２", 12)]
    [InlineData("5", 5)]
    [InlineData("O", 0)]
    [InlineData("⑤x", null)]
    [InlineData("１２３４", null)]
    [InlineData("", null)]
    public void NumericOcrFormsAreNormalizedWithoutAcceptingExtraText(string text, int? expected)
        => ShopEconomyReader.Number([new(text, (0, 0, 20, 20))]).Should().Be(expected);

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, null)]
    public async Task CircledPriceVotesAreCountedButConflictingPricesAreStillRejected(bool conflict, int? expected)
    {
        var ocr = new Mock<IOcrEngine>();
        ocr.Setup(e => e.RecognizeRegionAsync(It.IsAny<byte[]>(), 0, 0, 869, 73))
            .ReturnsAsync([new("回合1", (505, 25, 70, 21)), new("险境模拟", (163, 25, 76, 20))]);
        ocr.Setup(e => e.RecognizeRegionAsync(It.IsAny<byte[]>(), 0, 676, 869, 138))
            .ReturnsAsync([new("升级", (413, 729, 48, 28))]);
        var priceIndex = 0;
        ocr.Setup(e => e.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((byte[] _, int x, int y, int w, int h) =>
            {
                if (y < 100) return Task.FromResult<IReadOnlyList<OcrTextResult>>([]);
                var text = x > 1000 ? "4" : conflict ? new[] { "⑤", "⑥", "5", "6", "?" }[priceIndex++] : "⑤";
                return Task.FromResult<IReadOnlyList<OcrTextResult>>([new(text, (x, y, w, h))]);
            });
        var read = await new ShopEconomyReader(ocr.Object).ReadAsync([], 1449, 815);
        read.UpgradeCost.Should().Be(expected);
        read.Money.Should().Be(4);
        read.Round.Should().Be(1);
    }
}
