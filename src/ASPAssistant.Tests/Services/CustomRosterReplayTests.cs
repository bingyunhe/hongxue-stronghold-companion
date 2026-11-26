using ASPAssistant.Core.Data;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

[Collection("Native OCR")]
public class CustomRosterReplayTests
{
    [Fact, Trait("Category", "LocalScreenshot")]
    public async Task StationaryFramesSkipRepeatedOpeningOcr()
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_CUSTOM_ROSTER_FRAMES") is not { } dir) return;
        var shop = BmpPixels.TryRead(ImageBytes.ToBmp(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory,"TestData","shop-round1-multiplayer-1449x815.png"))))!;
        CustomRosterReader.MayBeOpening(shop).Should().BeFalse();
        var frames = new List<BmpPixels>();
        foreach (var name in new[]{"choosing","choosing-titlebar","one-each","empty","one-six","two-each"})
            frames.Add(BmpPixels.TryRead(ImageBytes.ToBmp(await File.ReadAllBytesAsync(Path.Combine(dir,name+".png"))))!);
        frames.Select(CustomRosterReader.OpeningKey).Skip(2).Should().OnlyHaveUniqueItems();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) CustomRosterReader.MayBeOpening(shop);
        var shopMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        int calls = 0;
        int? previous = null;
        foreach (var frame in frames)
            for (var tick = 0; tick < 100; tick++)
            {
                var key = CustomRosterReader.OpeningKey(frame);
                if (key != previous) calls++;
                previous = key;
            }
        calls.Should().BeInRange(5,6);
        Console.WriteLine($"shop gate 1000 frames {shopMs:F2}ms, ready keys 600 frames {clock.Elapsed.TotalMilliseconds:F2}ms, OCR dispatches {calls}");
    }

    [Theory, Trait("Category", "LocalScreenshot")]
    [InlineData("deck", CustomRosterPage.Catalogue, "予愿安洁莉娜,赤刃明霄陈,阿斯卡纶,百炼嘉维尔,凯尔希·思衡托,国度,丰川祥子,斩业星熊,维什戴尔,结城理,麒麟R夜刀,新约能天使")]
    [InlineData("choosing", CustomRosterPage.Choosing, "")]
    [InlineData("choosing-titlebar", CustomRosterPage.Choosing, "")]
    [InlineData("one-each", CustomRosterPage.Prepared, "予愿安洁莉娜,丰川祥子")]
    [InlineData("empty", CustomRosterPage.Prepared, "")]
    [InlineData("one-six", CustomRosterPage.Prepared, "丰川祥子")]
    [InlineData("two-each", CustomRosterPage.Prepared, "阿斯卡纶,凯尔希·思衡托,维什戴尔,麒麟R夜刀")]
    public async Task ReadFrame(string file, CustomRosterPage page, string names)
    {
        if (Environment.GetEnvironmentVariable("ASPASSISTANT_CUSTOM_ROSTER_FRAMES") is not { } dir) return;
        var path = Path.Combine(AppContext.BaseDirectory, "data");
        var ops = await new JsonDataStore(path).LoadCustomOperatorsAsync();
        using var ocr = new MaaOcrEngine(path);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, file + ".png"));
        var pixels = BmpPixels.TryRead(ImageBytes.ToBmp(bytes))!;
        CustomRosterReader.MayBeOpening(pixels).Should().Be(file != "deck");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new CustomRosterReader(ocr, ops).ReadAsync(bytes, pixels, file != "deck");
        Console.WriteLine($"{file} {watch.ElapsedMilliseconds}ms {System.Text.Json.JsonSerializer.Serialize(result)}");
        result.Should().NotBeNull();
        result!.Page.Should().Be(page);
        result.ConfirmedEmpty.Should().Be(file == "empty");
        result.Entries.Select(e => e.Name).Should().Equal(names.Split(',', StringSplitOptions.RemoveEmptyEntries));
        result.Unread.Should().BeEmpty();
        string[] deck = ["予愿安洁莉娜","赤刃明霄陈","阿斯卡纶","百炼嘉维尔","凯尔希·思衡托","国度","丰川祥子","斩业星熊","维什戴尔","结城理","麒麟R夜刀","新约能天使"];
        string[] skills = ["aglna2_2","chen3_3","ascln_2","gvial2_2","kalts2_2","dminon_1","oblvns_3","hsgma2_2","wisdel_3","makoto_1","yato2_3","angel2_2"];
        foreach (var entry in result.Entries)
        {
            var slot = Array.IndexOf(deck, entry.Name);
            entry.SkillId.Should().Be("skchr_" + skills[slot]);
            entry.Tier.Should().Be(slot < 6 ? 5 : 6);
        }
        if (file == "one-six") result.Entries.Single().Tier.Should().Be(6);
    }
}
