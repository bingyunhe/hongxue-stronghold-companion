using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPAssistant.App.Windows;
using ASPAssistant.Core.Data;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

[Collection("Update environment")]
public class MirrorChyanUpdateTests
{
    private const string Asset = "hongxue-stronghold-companion-v99.1.0-win-x64.zip";
    private const string Cdk = "example+/=&only";
    private const string Cdn = "https://cdn.test/update?expires=example";

    [Theory]
    [InlineData("v99.1.0", true)]
    [InlineData("v1.9.4", false)]
    [InlineData(null, false)]
    public async Task ChecksMirrorVersionBeforeDownloadingAndAllowsGitHubToBeAhead(string? tag, bool fromMirror)
    {
        var handler = new Feed(address => address.Host == "mirrorchyan.com"
            ? tag is null ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Mirror(tag)
            : GitHub());
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, new Version(1, 9, 4)) { DownloadCacheDirectory = Path.Combine(Path.GetTempPath(), "test-download-cache") };
        var info = await service.CheckForUpdateAsync();
        info.Should().NotBeNull();
        info!.TagName.Should().Be("v99.1.0");
        info.FromMirrorChyan.Should().Be(fromMirror);
        handler.Requests[0].Query.Should().Contain("os=win&arch=x64").And.NotContain("cdk=").And.Contain("current_version=v1.9.4");
        handler.Requests.Select(x => x.Host).Should()
            .Equal(fromMirror ? ["mirrorchyan.com"] : ["mirrorchyan.com", "api.github.com"]);
    }

    [Theory]
    [InlineData(1360000L, true, "v99.1.0", "预计下载 1.36 MB（增量包）", 1)]
    [InlineData(null, true, "v99.1.0", "GitHub 完整包参考大小 211.69 MB", 2)]
    [InlineData(1360000L, false, "v99.1.0", "GitHub 完整包参考大小 211.69 MB", 2)]
    [InlineData(null, true, "v99.2.0", "下载开始后确认大小", 2)]
    public async Task ShowsKnownSizeOrSameVersionReferenceWithoutRequestingCdk(
        long? mirrorSize, bool hasCdk, string githubTag, string expected, int requests)
    {
        var handler = new Feed(address => address.Host == "mirrorchyan.com"
            ? Mirror("v99.1.0", type: "incremental", size: mirrorSize)
            : GitHub(githubTag));
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, new Version(1, 9, 4));
        var info = await service.CheckForUpdateAsync(hasMirrorCdk: hasCdk);
        info!.DownloadSizeText.Should().Be(expected);
        handler.Requests.Should().HaveCount(requests);
        handler.Requests[0].Query.Should().NotContain("cdk=");
        if (requests == 2) handler.Requests[1].AbsolutePath.Should().EndWith("/tags/v99.1.0");
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("expired-link")]
    [InlineData("changed-version")]
    [InlineData("bad-hash")]
    public async Task DownloadsOnlyTheSelectedVersionAndChecksTheChosenSourcesHash(string scenario)
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var program = new StreamWriter(zip.CreateEntry("ASPAssistant.App.exe").Open());
            program.Write("update fixture, never executed");
        }
        var bytes = zipStream.ToArray();
        var validHash = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new Feed(address => address.Host switch
        {
            "mirrorchyan.com" when address.Query.Contains("cdk=") => scenario switch
            {
                "invalid" => new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { code = 7002, msg = Cdk }) },
                "changed-version" => Mirror("v99.2.0", Cdn, validHash),
                "bad-hash" => Mirror("v99.1.0", Cdn, new string('0', 64)),
                _ => Mirror("v99.1.0", Cdn, validHash),
            },
            "mirrorchyan.com" => Mirror("v99.1.0"),
            "cdn.test" when scenario == "expired-link" => new(HttpStatusCode.Forbidden),
            "cdn.test" => new() { Content = new ByteArrayContent(bytes) },
            "api.github.com" when address.AbsolutePath.EndsWith("/tags/v99.1.0", StringComparison.Ordinal) => GitHub(size: bytes.Length),
            "github.test" when address.AbsolutePath == "/checksum" => new() { Content = new StringContent($"{validHash}  {Asset}") },
            "github.test" => new() { Content = new ByteArrayContent(bytes) },
            _ => throw new InvalidOperationException("Unexpected test endpoint"),
        });
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, new Version(1, 9, 4)) { DownloadCacheDirectory = Path.Combine(Path.GetTempPath(), "test-download-cache") };
        var details = (await service.CheckForUpdateAsync())!;
        handler.Requests.Should().ContainSingle("displaying the update must not ask for a signed URL");
        var before = Directory.GetDirectories(Path.GetTempPath(), "ASPAssistant-Update-*").ToHashSet();
        try
        {
            if (scenario == "bad-hash")
            {
                Func<Task> download = () => service.DownloadAndApplyUpdateAsync(details, mirrorCdk: Cdk);
                await download.Should().ThrowAsync<UpdateIntegrityException>();
                service.PendingScriptPath.Should().BeNull();
                handler.Requests.Select(x => x.Host).Should().Equal("mirrorchyan.com", "mirrorchyan.com", "cdn.test");
                return;
            }

            await service.DownloadAndApplyUpdateAsync(details, mirrorCdk: scenario == "missing" ? null : Cdk);
            service.PendingScriptPath.Should().NotBeNull();
            Directory.Exists(service.PendingStagingDir).Should().BeFalse();
            var hosts = handler.Requests.Select(x => x.Host);
            if (scenario == "mirror")
            {
                hosts.Should().Equal("mirrorchyan.com", "mirrorchyan.com", "cdn.test");
                service.DownloadNotice.Should().BeNull();
            }
            else
            {
                hosts.Where(h => h is "api.github.com" or "github.test").Should()
                    .Equal("api.github.com", "github.test", "github.test");
                service.DownloadNotice.Should().NotContain(Cdk).And.NotContain(Cdn);
                if (scenario == "invalid") service.DownloadNotice.Should().Contain("无效");
                if (scenario == "expired-link") service.DownloadNotice.Should().Contain("链接已过期");
            }
            if (scenario != "missing")
                handler.Requests[1].Query.Should().Contain("cdk=" + Uri.EscapeDataString(Cdk)).And.Contain("current_version=v1.9.4");
            else
                handler.Requests.Should().NotContain(x => x.Query.Contains("cdk="));
        }
        finally
        {
            foreach (var directory in Directory.GetDirectories(Path.GetTempPath(), "ASPAssistant-Update-*").Except(before))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("changed-base")]
    [InlineData("missing-base")]
    [InlineData("bad-hash")]
    [InlineData("missing-payload")]
    [InlineData("wrong-version")]
    [InlineData("unsafe-deletion")]
    [InlineData("missing-cdn")]
    public async Task MirrorIncrementalPreparesExactTargetOrFetchesSameTagFullPackage(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("mirror-incremental-");
        var installed = root.CreateSubdirectory("installed").FullName;
        string? pending = null;
        try
        {
            File.WriteAllText(Path.Combine(installed, "obsolete"), "old");
            Directory.CreateDirectory(Path.Combine(installed, "old-dir"));
            File.WriteAllText(Path.Combine(installed, "old-dir", "old.txt"), "old");
            if (scenario != "missing-base")
                File.WriteAllText(Path.Combine(installed, "same"), scenario == "changed-base" ? "edited" : "unchanged");
            var personal = Path.Combine(root.FullName, "lineups.json");
            File.WriteAllText(personal, "my lineups");
            var files = new Dictionary<string, string>
            {
                ["ASPAssistant.App.exe"] = "target program", ["same"] = "unchanged", ["new-dir/add"] = "added"
            };
            string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
            string inventory = JsonSerializer.Serialize(new
            {
                Version = scenario == "wrong-version" ? "v99.2.0" : "v99.1.0",
                Files = files.ToDictionary(e => e.Key, e => Digest(Encoding.UTF8.GetBytes(e.Value))),
                Directories = Array.Empty<string>()
            });
            var patchFiles = new Dictionary<string, string>(files);
            patchFiles.Remove("same");
            if (scenario == "missing-payload") patchFiles.Remove("new-dir/add");
            patchFiles["mirror-files.json"] = inventory;
            patchFiles["changes.json"] = JsonSerializer.Serialize(new
            {
                added = new[] { "new-dir/add" }, modified = new[] { "ASPAssistant.App.exe", "mirror-files.json" },
                deleted = new[] { scenario == "unsafe-deletion" ? "../lineups.json" : "obsolete", "old-dir/old.txt" },
                added_dir = new[] { "new-dir" }, deleted_dir = new[] { "old-dir" }
            });
            byte[] Archive(Dictionary<string, string> contents)
            {
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                    foreach (var (name, data) in contents)
                    {
                        using var entry = zip.CreateEntry(name).Open();
                        entry.Write(Encoding.UTF8.GetBytes(data));
                    }
                return buffer.ToArray();
            }
            var patch = Archive(patchFiles);
            var full = Archive(files);
            var handler = new Feed(address => address.Host switch
            {
                "mirrorchyan.com" when !address.Query.Contains("cdk=") => Mirror("v99.1.0"),
                "mirrorchyan.com" when address.Query.Contains("current_version=v1.9.4") =>
                    Mirror("v99.1.0", "https://cdn.test/patch", scenario == "bad-hash" ? new string('0', 64) : Digest(patch), "incremental"),
                "mirrorchyan.com" when !address.Query.Contains("current_version") =>
                    Mirror("v99.1.0", "https://cdn.test/full", Digest(full)),
                "cdn.test" when address.AbsolutePath == "/patch" && scenario == "missing-cdn" => new(HttpStatusCode.NotFound),
                "cdn.test" => new() { Content = new ByteArrayContent(address.AbsolutePath == "/patch" ? patch : full) },
                _ => throw new InvalidOperationException("Unexpected endpoint")
            });
            using var http = new HttpClient(handler);
            var service = new UpdateService(http, new Version(1, 9, 4, 0))
            {
                MainDirectory = installed, DownloadCacheDirectory = root.CreateSubdirectory("cache").FullName
            };
            var info = (await service.CheckForUpdateAsync())!;
            await service.DownloadAndApplyUpdateAsync(info, mirrorCdk: Cdk);
            pending = Path.GetDirectoryName(service.PendingScriptPath!)!;
            var output = Path.Combine(pending, "extracted");
            foreach (var (name, data) in files)
                File.ReadAllText(Path.Combine(output, name)).Should().Be(data);
            File.Exists(Path.Combine(output, "obsolete")).Should().BeFalse();
            Directory.Exists(Path.Combine(output, "old-dir")).Should().BeFalse();
            handler.Requests.Count(u => u.AbsolutePath == "/full").Should().Be(scenario == "valid" ? 0 : 1);
            File.ReadAllText(Path.Combine(installed, "obsolete")).Should().Be("old");
            File.ReadAllText(personal).Should().Be("my lineups");
        }
        finally
        {
            if (pending is not null) Directory.Delete(pending, true);
            root.Delete(true);
        }
    }

    [Fact]
    public async Task CdkIsEncryptedAndExcludedFromTheConfigurationArchive()
    {
        var directory = Directory.CreateTempSubdirectory("mirror-settings-").FullName;
        try
        {
            var store = new MirrorChyanCdkStore(directory);
            (await store.LoadAsync()).Should().BeNull();
            await store.SaveAsync($" {Cdk} ");
            (await new MirrorChyanCdkStore(directory).LoadAsync()).Should().Be(Cdk);
            Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(directory, "mirrorchyan.cdk"))).Should().NotContain(Cdk);
            var archive = Path.Combine(directory, "settings.zip");
            ConfigurationArchive.Export(directory, archive, ConfigurationScope.Full);
            using var zip = ZipFile.OpenRead(archive);
            zip.Entries.Should().NotContain(x => x.FullName.Contains("mirrorchyan.cdk", StringComparison.OrdinalIgnoreCase));
            await store.SaveAsync("");
            store.HasSavedCdk.Should().BeFalse();
            (await store.LoadAsync()).Should().BeNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static HttpResponseMessage Mirror(string tag, string? address = null, string? hash = null, string type = "full",
        long? size = 211691279) =>
        new() { Content = JsonContent.Create(new { code = 0, data = new { version_name = tag, update_type = type, release_note = "测试", url = address, sha256 = hash, filesize = address is null ? size : null } }) };

    private static HttpResponseMessage GitHub(string tag = "v99.1.0", long size = 211691279) => new()
    {
        Content = JsonContent.Create(new
        {
            tag_name = tag,
            body = "GitHub",
            assets = new[]
            {
                new { name = Asset, browser_download_url = "https://github.test/package", size },
                new { name = "SHA256SUMS.txt", browser_download_url = "https://github.test/checksum", size = 500L },
            },
        })
    };

    private sealed class Feed(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
