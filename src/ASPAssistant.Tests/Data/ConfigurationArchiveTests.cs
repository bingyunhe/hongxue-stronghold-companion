using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Models;
using FluentAssertions;

namespace ASPAssistant.Tests.Data;

[Collection("Application logging")]
public sealed class ConfigurationArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "configuration-" + Guid.NewGuid().ToString("N"));
    public ConfigurationArchiveTests() => Directory.CreateDirectory(_root);

    private string Profile(string name, double scale)
    {
        var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.Combine(path, "lineups", "order"));
        File.WriteAllText(Path.Combine(path, "appearance.json"), JsonSerializer.Serialize(new SidebarAppearance { UiScale = scale }));
        File.WriteAllText(Path.Combine(path, "custom-operators.json"), "[\"风絮\"]");
        File.WriteAllText(Path.Combine(path, "tracking.json"), JsonSerializer.Serialize(new TrackingState { PurchasedEquipment = [name] }));
        File.WriteAllText(Path.Combine(path, "terminal-level.json"), "{\"Level\":2,\"SavedAtUtc\":\"2026-11-18T00:00:00Z\"}");
        File.WriteAllText(Path.Combine(path, "lineups", "one.json"), JsonSerializer.Serialize(new Lineup { Id = "one", Name = name }));
        File.WriteAllText(Path.Combine(path, "lineups", "order", "lineups.json"), "[\"one\"]");
        File.WriteAllText(Path.Combine(path, "unrelated.txt"), "unrelated personal file");
        Directory.CreateDirectory(Path.Combine(path, "configuration-backups"));
        File.WriteAllText(Path.Combine(path, "configuration-backups", "old.zip"), "not included");
        return path;
    }

    [Theory]
    [InlineData(ConfigurationScope.Full)]
    [InlineData(ConfigurationScope.Software)]
    [InlineData(ConfigurationScope.Lineups)]
    public void RoundTripKeepsScopeStagesUntilStartupAndBacksUpCurrentData(ConfigurationScope scope)
    {
        var source = Profile("source", 1.25); var destination = Profile("destination", 1);
        const string filterFile = "automatic-current-run-filters.json";
        File.WriteAllText(Path.Combine(source, filterFile), "true");
        File.WriteAllText(Path.Combine(destination, filterFile), "false");
        var zip = Path.Combine(_root, scope + ".zip");
        ConfigurationArchive.Export(source, zip, scope);
        using (var archive = ZipFile.OpenRead(zip))
        {
            archive.Entries.Should().NotContain(entry => entry.FullName.Contains("unrelated") || entry.FullName.Contains("old.zip"));
            archive.Entries.Any(entry => entry.FullName == "files/tracking.json").Should().Be(scope == ConfigurationScope.Full);
            archive.Entries.Any(entry => entry.FullName == "files/appearance.json").Should().Be(scope != ConfigurationScope.Lineups);
            archive.Entries.Any(entry => entry.FullName == "files/" + filterFile).Should().Be(scope != ConfigurationScope.Lineups);
            archive.Entries.Any(entry => entry.FullName == "files/lineups/one.json").Should().Be(scope != ConfigurationScope.Software);
        }
        var oldAppearance = File.ReadAllBytes(Path.Combine(destination, "appearance.json"));
        ConfigurationArchive.StageImport(destination, zip);
        ConfigurationArchive.HasPendingImport(destination).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(destination, "appearance.json")).Should().Equal(oldAppearance, "staging must not overwrite live settings");
        var result = ConfigurationArchive.ApplyPending(destination)!;
        result.Succeeded.Should().BeTrue(); ConfigurationArchive.HasPendingImport(destination).Should().BeFalse();
        using (var backup = ZipFile.OpenRead(result.BackupPath!))
        {
            using var stream = backup.GetEntry("files/appearance.json")!.Open();
            using var bytes = new MemoryStream(); stream.CopyTo(bytes); bytes.ToArray().Should().Equal(oldAppearance);
        }
        var appearance = JsonSerializer.Deserialize<SidebarAppearance>(File.ReadAllText(Path.Combine(destination, "appearance.json")))!;
        appearance.UiScale.Should().Be(scope == ConfigurationScope.Lineups ? 1 : 1.25);
        File.ReadAllText(Path.Combine(destination, filterFile)).Should().Be(scope == ConfigurationScope.Lineups ? "false" : "true");
        var lineup = JsonSerializer.Deserialize<Lineup>(File.ReadAllText(Path.Combine(destination, "lineups", "one.json")))!;
        lineup.Name.Should().Be(scope == ConfigurationScope.Software ? "destination" : "source");
        var tracking = JsonSerializer.Deserialize<TrackingState>(File.ReadAllText(Path.Combine(destination, "tracking.json")))!;
        tracking.PurchasedEquipment.Should().Equal(scope == ConfigurationScope.Full ? "source" : "destination");
        File.ReadAllText(Path.Combine(destination, "unrelated.txt")).Should().Be("unrelated personal file");
        ConfigurationArchive.ApplyPending(destination).Should().BeNull();
    }

    [Fact]
    public void ExportWillNotOverwriteAnExistingUserFile()
    {
        var source = Profile("source", 1); var target = Path.Combine(_root, "existing.zip"); File.WriteAllText(target, "original");
        Action export = () => ConfigurationArchive.Export(source, target, ConfigurationScope.Full);
        export.Should().Throw<IOException>(); File.ReadAllText(target).Should().Be("original");
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("lineups/../../outside.json")]
    [InlineData("C:/outside.json")]
    [InlineData("lineups/order/unknown.json")]
    [InlineData("lineups/invalid?.json")]
    public void ImportRejectsPathsOutsideTheDeclaredConfigurationScope(string entry)
    {
        var package = Path.Combine(_root, "invalid.zip"); WritePackage(package, entry, "{}");
        var destination = Profile("destination", 1);
        Action stage = () => ConfigurationArchive.StageImport(destination, package);
        stage.Should().Throw<InvalidDataException>(); ConfigurationArchive.HasPendingImport(destination).Should().BeFalse();
    }

    [Theory]
    [InlineData("appearance.json", "{\"UiScale\":\"bad\"}")]
    [InlineData("tracking.json", "{\"Entries\":null}")]
    [InlineData("custom-operators.json", "[null]")]
    [InlineData("automatic-purchases.json", "\"yes\"")]
    [InlineData("automatic-current-run-filters.json", "\"yes\"")]
    [InlineData("lineups/one.json", "{\"slots\":null}")]
    public void ImportRejectsMalformedConfigurationBeforeTouchingUserData(string entry, string content)
    {
        var package = Path.Combine(_root, "malformed.zip"); WritePackage(package, entry, content);
        var destination = Profile("destination", 1);
        Action stage = () => ConfigurationArchive.StageImport(destination, package);
        stage.Should().Throw<Exception>().Where(error => error is IOException || error is JsonException || error is InvalidDataException);
        ConfigurationArchive.HasPendingImport(destination).Should().BeFalse();
    }

    [Fact]
    public void ChangedChecksumCannotReplaceAnAlreadyStagedImport()
    {
        var source = Profile("source", 1.25); var destination = Profile("destination", 1);
        var archive = Path.Combine(_root, "source.zip"); ConfigurationArchive.Export(source, archive, ConfigurationScope.Software);
        ConfigurationArchive.StageImport(destination, archive);
        var pending = File.ReadAllBytes(Path.Combine(destination, "pending-configuration.zip"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("files/appearance.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("files/appearance.json").Open()); writer.Write("{}");
        }
        Action stage = () => ConfigurationArchive.StageImport(destination, archive);
        stage.Should().Throw<InvalidDataException>();
        File.ReadAllBytes(Path.Combine(destination, "pending-configuration.zip")).Should().Equal(pending);
    }

    [Fact]
    public void ReadOnlyTargetRollsBackEarlierReplacementsAndRetainsBackup()
    {
        var source = Profile("source", 1.25); var destination = Profile("destination", 1);
        var archive = Path.Combine(_root, "full.zip"); ConfigurationArchive.Export(source, archive, ConfigurationScope.Full);
        ConfigurationArchive.StageImport(destination, archive);
        var appearance = File.ReadAllBytes(Path.Combine(destination, "appearance.json"));
        var trackingPath = Path.Combine(destination, "tracking.json"); File.SetAttributes(trackingPath, FileAttributes.ReadOnly);
        try
        {
            var result = ConfigurationArchive.ApplyPending(destination)!;
            result.Succeeded.Should().BeFalse(); File.Exists(result.BackupPath).Should().BeTrue();
            File.ReadAllBytes(Path.Combine(destination, "appearance.json")).Should().Equal(appearance);
            ConfigurationArchive.HasPendingImport(destination).Should().BeFalse();
        }
        finally { File.SetAttributes(trackingPath, FileAttributes.Normal); }
    }

    [Fact]
    public void OversizedEntryIsRejectedBeforeExtraction()
    {
        var archive = Path.Combine(_root, "large.zip"); WritePackage(archive, "appearance.json", new string(' ', 8 * 1024 * 1024 + 1));
        Action stage = () => ConfigurationArchive.StageImport(Path.Combine(_root, "destination"), archive);
        stage.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void InvalidQueuedPackageIsRetainedWithoutBlockingFutureStartup()
    {
        var destination = Profile("destination", 1);
        File.WriteAllText(Path.Combine(destination, "pending-configuration.zip"), "not a zip file");
        var before = File.ReadAllBytes(Path.Combine(destination, "appearance.json"));
        ConfigurationArchive.ApplyPending(destination)!.Succeeded.Should().BeFalse();
        ConfigurationArchive.HasPendingImport(destination).Should().BeFalse();
        File.ReadAllBytes(Path.Combine(destination, "appearance.json")).Should().Equal(before);
        Directory.GetFiles(Path.Combine(destination, "configuration-backups"), "未应用配置-*.zip").Should().ContainSingle();
    }

    [Fact]
    public void MeasureConfigurationArchiveCostWithOneHundredLineups()
    {
        var source = Profile("source", 1.25);
        for (var i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(source, "lineups", $"saved-{i}.json"), JsonSerializer.Serialize(new Lineup
            {
                Id = $"saved-{i}", Name = $"保存阵容{i}", Slots = Enumerable.Range(0, 15).Select(n => new LineupSlot { OperatorName = $"干员{n}" }).ToList()
            }));
        var destination = Profile("destination", 1);
        var package = Path.Combine(_root, "performance.zip");
        var allocated = GC.GetTotalAllocatedBytes();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ConfigurationArchive.Export(source, package, ConfigurationScope.Full); var exported = watch.Elapsed.TotalMilliseconds;
        ConfigurationArchive.StageImport(destination, package); var staged = watch.Elapsed.TotalMilliseconds - exported;
        ConfigurationArchive.ApplyPending(destination)!.Succeeded.Should().BeTrue();
        var applied = watch.Elapsed.TotalMilliseconds - exported - staged;
        Directory.CreateDirectory(TestOutput.Directory);
        File.WriteAllText(Path.Combine(TestOutput.Directory, "configuration-performance.json"), JsonSerializer.Serialize(new
        {
            Lineups = 101, PackageBytes = new FileInfo(package).Length, ExportMilliseconds = exported, StageMilliseconds = staged,
            ApplyMilliseconds = applied, TotalAllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, AdditionalOcrCalls = 0
        }));
    }

    private static void WritePackage(string path, string relative, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("configuration-manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(new { FormatVersion = 1, Scope = 0, Files = new[] { new { Path = relative, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } } }));
        using var output = zip.CreateEntry("files/" + relative).Open(); output.Write(bytes);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
