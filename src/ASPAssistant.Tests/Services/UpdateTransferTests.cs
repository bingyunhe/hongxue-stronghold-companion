using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

[Collection("Update environment")]
public sealed class UpdateTransferTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData("small")]
    [InlineData("resume")]
    [InlineData("ignored")]
    [InlineData("wrong-range")]
    [InlineData("expired-mirror")]
    [InlineData("corrupt")]
    public async Task DownloadUsesVerifiedBytesAcrossDisconnectsAndRestart(string scenario)
    {
        var cache = Directory.CreateTempSubdirectory("transfer-fixture-");
        var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            using var output = zip.CreateEntry("ASPAssistant.App.exe", CompressionLevel.NoCompression).Open();
            var block = new byte[MiB];
            Random.Shared.NextBytes(block);
            for (int i = 0; i < (scenario == "small" ? 1 : 40); i++) output.Write(block);
        }
        var bytes = data.ToArray();
        var cut = bytes.Length * 4 / 5;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var offsets = new List<long>();
        var status = new List<string>();
        var transfer = new List<UpdateTransferProgress>();
        var mirrorQueries = 0;
        long delivered = 0;
        var name = "test-win-x64.zip";
        var info = new UpdateInfo("v9.0.0", "9.0.0", "", "https://fixture.test/package", name, "https://fixture.test/sums")
        { FullSize = bytes.Length, FromMirrorChyan = scenario == "expired-mirror" };
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "mirrorchyan.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { code = 0, data = new { version_name = "9.0.0", update_type = "full", sha256 = hash, url = $"https://cdn.test/{++mirrorQueries}" } }) };
            if (uri.AbsolutePath == "/sums") return new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {name}") };
            long start = request.Headers.Range?.Ranges.Single().From ?? 0;
            offsets.Add(start);
            if (offsets.Count == 2 && scenario is "resume" or "corrupt")
                throw new HttpRequestException(HttpRequestError.ConnectionError, "offline");
            if (scenario == "expired-mirror" && offsets.Count == 2)
                return new(HttpStatusCode.Forbidden);
            bool ranged = start > 0 && scenario != "ignored";
            int from = ranged ? (int)start : 0;
            int length = offsets.Count == 1 ? cut : bytes.Length - from;
            HttpContent body = ranged && scenario == "resume"
                ? new StreamContent(new DelayedStream(bytes, from, length))
                : new ByteArrayContent(bytes, from, length);
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = body };
            body.Headers.ContentLength = bytes.Length - from;
            if (ranged) body.Headers.ContentRange = new ContentRangeHeaderValue(
                scenario == "wrong-range" ? from - 1 : from, bytes.Length - 1, bytes.Length);
            if (!(ranged && scenario == "wrong-range")) delivered += length;
            return response;
        }));
        var service = new UpdateService(client, new Version(1, 0, 0)) { DownloadCacheDirectory = cache.FullName };
        service.TransferStatusChanged += status.Add;
        service.TransferProgressChanged += transfer.Add;
        try
        {
            if (scenario is "resume" or "corrupt")
            {
                Func<Task> interrupted = () => service.DownloadAndApplyUpdateAsync(info);
                await interrupted.Should().ThrowAsync<HttpRequestException>();
                service.HasSavedDownload.Should().BeTrue();
                var saved = cache.GetFiles("*.part").Single();
                saved.Length.Should().Be(cut);
                if (scenario == "corrupt")
                {
                    using var file = saved.Open(FileMode.Open, FileAccess.Write);
                    file.WriteByte(0);
                }
                transfer.Clear();
                service = new UpdateService(client, new Version(1, 0, 0)) { DownloadCacheDirectory = cache.FullName };
                service.TransferStatusChanged += status.Add;
                service.TransferProgressChanged += transfer.Add;
            }
            Func<Task> finish = () => service.DownloadAndApplyUpdateAsync(info, mirrorCdk: info.FromMirrorChyan ? "test-only" : null);
            if (scenario == "corrupt")
            {
                await finish.Should().ThrowAsync<UpdateIntegrityException>();
                service.PendingScriptPath.Should().BeNull();
            }
            else
            {
                await finish();
                var job = Path.GetDirectoryName(service.PendingScriptPath!)!;
                (await UpdateService.ComputeSha256Async(Path.Combine(job, name), default)).Should().Be(hash.ToLowerInvariant());
            }
            cache.GetFiles("*.part").Should().BeEmpty();
            offsets.Should().Equal(scenario switch
            {
                "small" => new long[] { 0, 0 },
                "ignored" => [0, cut],
                "wrong-range" => [0, cut, 0],
                _ => [0, cut, cut]
            });
            if (scenario is "resume" or "expired-mirror") delivered.Should().Be(bytes.Length);
            if (scenario == "resume")
            {
                transfer[0].Should().Be(new UpdateTransferProgress(cut, bytes.Length, null));
                transfer[1].ReceivedBytes.Should().Be(cut + 65536);
                transfer[1].BytesPerSecond.Should().BeInRange(1, 65536);
                service.TransferProgress.Should().BeNull("verification follows the transfer");
            }
            if (scenario == "expired-mirror") mirrorQueries.Should().Be(3);
            status.Count(m => m.Contains("3 秒")).Should().Be(1);
        }
        finally
        {
            if (service.PendingScriptPath is { } script) Directory.Delete(Path.GetDirectoryName(script)!, true);
            cache.Delete(true);
        }
    }

    private sealed class DelayedStream(byte[] bytes, int start, int length) : MemoryStream(bytes, start, length)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) await Task.Delay(1100, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
