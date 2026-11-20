using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using ASPAssistant.Core;
using ASPAssistant.Core.GameModes.GarrisonProtocol;
using ASPAssistant.Core.Services;
using FluentAssertions;
using Moq;

namespace ASPAssistant.Tests.Services;

[Collection("Application logging")]
public sealed class FeedbackReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedback-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _logPath = typeof(AppLogger).GetField("_logFilePath", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo _presence = typeof(AppLogger).GetField("_isGamePresent", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly FieldInfo _snapshot = typeof(RecognitionDiagnostics).GetField("_latest", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousLog;
    private readonly object? _previousPresence;
    private readonly object? _previousSnapshot;
    public FeedbackReportTests()
    {
        _previousLog = _logPath.GetValue(null); _previousPresence = _presence.GetValue(null); _previousSnapshot = _snapshot.GetValue(null);
        _snapshot.SetValue(null, null);
        AppLogger.Initialize(_directory);
    }

    [Fact]
    public void BusinessFailuresAreRecordedWithoutGameAndRecognitionErrorsAreRateLimited()
    {
        AppLogger.SetGamePresenceCheck(static () => false);
        AppLogger.Warn("Lineup", "Import failed: local proxy refused 127.0.0.1:7890");
        AppLogger.Warn("Update", "Download failed");
        for (var i = 0; i < 100; i++) AppLogger.RecognitionFailure("FeedbackTest", "same-job", "job failed");
        var text = File.ReadAllText(AppLogger.LogFilePath!);
        text.Should().Contain("Import failed").And.Contain("Download failed");
        text.Split("[FeedbackTest]").Should().HaveCount(2);
    }

    [Theory]
    [InlineData("C:\\Users\\tester\\AppData\\file.json", "tester")]
    [InlineData("D:/private/source.cs:line 99", "private")]
    [InlineData("https://example.com/api?token=sample-sensitive&user=abc", "sample-sensitive")]
    [InlineData("Authorization: Bearer sample-sensitive", "sample-sensitive")]
    [InlineData("Cookie: session=sample-sensitive", "sample-sensitive")]
    [InlineData("api_key=sample-sensitive", "sample-sensitive")]
    [InlineData("https://user:sample-sensitive@proxy.example:7890", "sample-sensitive")]
    public void PreviewRemovesLocalPathsCredentialsAndUrlParameters(string input, string hidden)
    {
        FeedbackReport.Sanitize(input).Should().NotContain(hidden);
        FeedbackReport.Sanitize("无法连接 127.0.0.1:7890").Should().Contain("127.0.0.1:7890");
        FeedbackReport.Sanitize("https://example.com/api?user=abc").Should().StartWith("https://example.com/api?");
    }

    [Fact]
    public void ExportContainsOnlyReviewedTextAndAnExplicitlySelectedFrozenScreenshot()
    {
        var now = DateTimeOffset.Now;
        var frame = new byte[] { 1, 2, 3 };
        RecognitionDiagnostics.Record(now, "商店识别", frame, new { Name = "风絮", Region = new { X = .1, Y = .8 } });
        var report = FeedbackReport.Prepare();
        RecognitionDiagnostics.Record(now.AddMilliseconds(1), "商店识别", [4, 5], new { Name = "仇白" });
        report.Recognition!.Frame.Should().BeSameAs(frame);
        var path = Path.Combine(_directory, "report.zip");
        report.Export(path, "识别有误", "风絮未高亮", "打开商店", false);
        using (var zip = ZipFile.OpenRead(path))
        {
            zip.Entries.Select(x => x.FullName).Should().Equal("反馈说明.txt");
            using var reader = new StreamReader(zip.Entries[0].Open());
            reader.ReadToEnd().Should().Be(report.Preview("识别有误", "风絮未高亮", "打开商店"));
        }
        var withImage = Path.Combine(_directory, "with-image.zip");
        report.Export(withImage, "识别有误", "风絮未高亮", "打开商店", true);
        using var included = ZipFile.OpenRead(withImage);
        included.Entries.Should().HaveCount(2);
        using var image = included.GetEntry("识别画面.png")!.Open();
        using var bytes = new MemoryStream(); image.CopyTo(bytes);
        bytes.ToArray().Should().Equal(frame);
        Action overwrite = () => report.Export(path, "程序报错", "描述", "步骤", false);
        overwrite.Should().Throw<IOException>();
    }

    [Fact]
    public void PreparationBoundsLargeLogsAndKeepsTheLatestFailure()
    {
        File.WriteAllText(AppLogger.LogFilePath!, new string('x', 3 * 1024 * 1024) + "\n[ERROR] latest failure\n");
        var report = FeedbackReport.Prepare();
        report.Logs.Should().Contain("仅包含末尾").And.Contain("latest failure");
        report.Logs.Length.Should().BeLessThan(2 * 1024 * 1024 + 100);
    }

    [Fact]
    public void MissingLogStillAllowsFeedbackWithoutExposingItsLocalPath()
    {
        File.Delete(AppLogger.LogFilePath!);
        var report = FeedbackReport.Prepare();
        report.Logs.Should().Contain("无法读取日志").And.NotContain(_directory);
        report.Export(Path.Combine(_directory, "without-log.zip"), "程序报错", "无法启动", "", false);
        File.Exists(Path.Combine(_directory, "without-log.zip")).Should().BeTrue();
    }

    [Fact]
    public void LateRecognitionCannotReplaceNewerFrameAndOversizeImagesAreNotRetained()
    {
        var now = DateTimeOffset.Now;
        RecognitionDiagnostics.Record(now, "new", [2], new { Status = "new" });
        RecognitionDiagnostics.Record(now.AddSeconds(-1), "late", [1], new { Status = "old" });
        RecognitionDiagnostics.Latest!.Source.Should().Be("new");
        RecognitionDiagnostics.Record(now.AddSeconds(1), "large", new byte[RecognitionDiagnostics.MaximumFrameBytes + 1], new { Status = "too large" });
        RecognitionDiagnostics.Latest!.Frame.Should().BeEmpty();
        RecognitionDiagnostics.Latest.Result.Should().Contain("too large");
    }

    [Fact]
    public async Task ScannerPairsOneCapturedFrameWithResultsWithoutAdditionalCapturesOrOcr()
    {
        using var bitmap = new Bitmap(960, 540);
        using var image = new MemoryStream(); bitmap.Save(image, ImageFormat.Png);
        var frame = image.ToArray();
        var captures = 0;
        var engine = new Mock<IOcrEngine>();
        engine.Setup(x => x.RecognizeRegionAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        engine.Setup(x => x.RecognizeLineAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<OcrTextResult>());
        using var scanner = new OcrScannerService(() => { captures++; return frame; }, new GarrisonOcrStrategy(),
            new ShopReader(engine.Object, []), engine.Object, new(), []);
        await scanner.ScanNowAsync();
        captures.Should().Be(1);
        RecognitionDiagnostics.Latest!.Frame.Should().BeSameAs(frame);
        RecognitionDiagnostics.Latest.Result.Should().Contain("未检测到展开的商店");
        var calls = engine.Invocations.Count;
        var report = FeedbackReport.Prepare();
        report.Export(Path.Combine(_directory, "scanner.zip"), "识别有误", "漏识别", "", true);
        captures.Should().Be(1);
        engine.Invocations.Count.Should().Be(calls, "preparation and export reuse the frame and never run OCR");
    }

    [Fact]
    public void FrozenEventExportsOrderedPreviewedFramesAndOmitsImagesWhenUnchecked()
    {
        var history = new RecognitionSampleHistory();
        var id = history.Begin("干员复核 · D8", 8);
        history.Step(id, "点击前", [0x42, 0x4d, 1]);
        history.Step(id, "目标 D8 · 点击 900,390");
        history.Step(id, "选中 D9", [0x89, 0x50, 2]);
        var selected = Assert.Single(history.Freeze());
        history.Step(id, "后续扫描");
        var report = new FeedbackReport("本机版本", "[LiveField] selected=D9\n[Private] unrelated", null) { Selected = selected };
        var preview = report.Preview("识别有误","", "");
        Assert.Contains("目标 D8", preview);
        Assert.Contains("选中 D9", preview);
        Assert.DoesNotContain("unrelated", preview);
        Assert.DoesNotContain("后续扫描", preview);
        foreach (var include in new[] { true, false })
        {
            var path = Path.Combine(_directory, $"event-{include}.zip");
            report.Export(path, "识别有误", "", "", include);
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(include ? 4 : 1, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("反馈说明.txt")!.Open());
            Assert.Equal(preview, reader.ReadToEnd());
            for (int i = 0; include && i < selected.Frames.Length; i++)
            {
                using var manifest = JsonDocument.Parse(zip.GetEntry("recognition.json")!.Open());
                var pathInZip = manifest.RootElement.GetProperty("frames")[i].GetProperty("file").GetString()!;
                using var encoded = zip.GetEntry(pathInZip)!.Open();
                using var bytes = new MemoryStream();
                encoded.CopyTo(bytes);
                Assert.Equal(selected.Frames[i].Image, bytes.ToArray());
                Assert.EndsWith(selected.Frames[i].Extension, pathInZip);
            }
        }
    }

    [Fact]
    public void SampleBuffersKeepRecentFramesAndEnforceTheirByteLimits()
    {
        var history = new RecognitionSampleHistory();
        var frame = new byte[12 * 1024 * 1024];
        for (int i = 0; i < 18; i++)
        {
            history.Observe(frame, 4, DateTimeOffset.Now);
            var id = history.Begin($"事件{i}", 4, true);
            history.Step(id, "核对", frame);
        }
        var groups = history.Freeze();
        Assert.Equal("事件17", groups[0].Kind);
        Assert.InRange(groups[..^1].Sum(s => s.Frames.Sum(f => f.Image.Length)), 1, RecognitionSampleHistory.EventBytes);
        Assert.InRange(groups[^1].Frames.Sum(f => f.Image.Length), 1, RecognitionSampleHistory.RecentBytes);
        history.ClearRecent();
        Assert.DoesNotContain(history.Freeze(), s => s.Kind == "最近游戏画面");
    }

    [Fact]
    public void MeasureSnapshotRetentionCost()
    {
        var frame = new byte[1024 * 1024];
        var result = new { Status = "完成", Items = new[] { new { Name = "风絮", X = .1, Y = .8, Width = .1, Height = .03 } }, RawText = new string('识', 500) };
        var now = DateTimeOffset.Now;
        RecognitionDiagnostics.Record(now, "benchmark", frame, result);
        const int count = 2000;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= count; i++) RecognitionDiagnostics.Record(now.AddTicks(i), "benchmark", frame, result);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        RecognitionDiagnostics.Latest!.Frame.Should().BeSameAs(frame);
        var evidence = new { Iterations = count, AddedMillisecondsPerFrame = watch.Elapsed.TotalMilliseconds / count,
            AllocatedBytesPerFrame = allocated / count, RetainedFrameBytes = frame.Length, MaximumRetainedFrameBytes = RecognitionDiagnostics.MaximumFrameBytes,
            AdditionalCaptureCalls = 0, AdditionalOcrCalls = 0 };
        Directory.CreateDirectory(TestOutput.Directory);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "feedback-performance.json"), JsonSerializer.Serialize(evidence));
    }

    public void Dispose()
    {
        _logPath.SetValue(null, _previousLog); _presence.SetValue(null, _previousPresence); _snapshot.SetValue(null, _previousSnapshot);
        Directory.Delete(_directory, true);
    }
}
