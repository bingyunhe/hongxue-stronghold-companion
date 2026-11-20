using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
        var service = new UpdateService(http, new Version(1, 9, 4));
        var info = await service.CheckForUpdateAsync();
        info.Should().NotBeNull();
        info!.TagName.Should().Be("v99.1.0");
        info.FromMirrorChyan.Should().Be(fromMirror);
        handler.Requests[0].Query.Should().Contain("os=win&arch=x64").And.NotContain("cdk=").And.NotContain("current_version=");
        handler.Requests.Select(x => x.Host).Should()
            .Equal(fromMirror ? ["mirrorchyan.com"] : ["mirrorchyan.com", "api.github.com"]);
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
            "api.github.com" when address.AbsolutePath.EndsWith("/tags/v99.1.0", StringComparison.Ordinal) => GitHub(),
            "github.test" when address.AbsolutePath == "/checksum" => new() { Content = new StringContent($"{validHash}  {Asset}") },
            "github.test" => new() { Content = new ByteArrayContent(bytes) },
            _ => throw new InvalidOperationException("Unexpected test endpoint"),
        });
        using var http = new HttpClient(handler);
        var service = new UpdateService(http, new Version(1, 9, 4));
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
                handler.Requests[1].Query.Should().Contain("cdk=" + Uri.EscapeDataString(Cdk)).And.NotContain("current_version=");
            else
                handler.Requests.Should().NotContain(x => x.Query.Contains("cdk="));
        }
        finally
        {
            foreach (var directory in Directory.GetDirectories(Path.GetTempPath(), "ASPAssistant-Update-*").Except(before))
                Directory.Delete(directory, true);
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

    private static HttpResponseMessage Mirror(string tag, string? address = null, string? hash = null) =>
        new() { Content = JsonContent.Create(new { code = 0, data = new { version_name = tag, release_note = "测试", url = address, sha256 = hash } }) };

    private static HttpResponseMessage GitHub() => new()
    {
        Content = JsonContent.Create(new
        {
            tag_name = "v99.1.0",
            body = "GitHub",
            assets = new[]
            {
                new { name = Asset, browser_download_url = "https://github.test/package" },
                new { name = "SHA256SUMS.txt", browser_download_url = "https://github.test/checksum" },
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
