using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ASPAssistant.Core.Services;

public sealed record UpdateTransferProgress(long ReceivedBytes, long TotalBytes, double? BytesPerSecond);

public partial class UpdateService
{
    private const long ResumeThreshold = 32L * 1024 * 1024;
    internal string DownloadCacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HongxueCompanion", "UpdateCache");

    public string? TransferStatus { get; private set; }
    public UpdateTransferProgress? TransferProgress { get; private set; }
    public bool HasSavedDownload { get; private set; }
    public event Action<string>? TransferStatusChanged;
    public event Action<UpdateTransferProgress>? TransferProgressChanged;

    private void SetTransferStatus(string message)
    {
        TransferStatus = message;
        TransferProgress = null;
        TransferStatusChanged?.Invoke(message);
    }

    private static bool TransientDownloadError(Exception error) => error is
        OperationCanceledException or IOException { InnerException: System.Net.Sockets.SocketException } or HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError } or
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } or
        HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded };

    private async Task DownloadPackageAsync(Uri address, string zipPath, string version, string hash, long size,
        IProgress<double>? progress, CancellationToken ct)
    {
        var identity = Encoding.UTF8.GetBytes($"{version}\n{Path.GetFileName(zipPath)}\n{hash.ToUpperInvariant()}");
        Directory.CreateDirectory(DownloadCacheDirectory);
        var part = Path.Combine(DownloadCacheDirectory, Convert.ToHexString(SHA256.HashData(identity)) + ".part");
        using var taskLock = new FileStream(part + ".lock", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var saved = new FileInfo(part);
        long offset = saved.Exists ? saved.Length : 0;
        HasSavedDownload = offset > 0;
        if (offset > 0 && offset == size)
        {
            File.Move(part, zipPath, true);
            HasSavedDownload = false;
            TransferStatus = null;
            return;
        }
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var range = response.Content.Headers.ContentRange;
            bool partial = response.StatusCode == HttpStatusCode.PartialContent;
            if (partial && (offset == 0 || range?.Unit != "bytes" || range.From != offset ||
                range.Length is null || range.To != range.Length - 1 || (size > 0 && range.Length != size)) ||
                offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (offset == 0) throw new InvalidDataException("下载源返回的文件范围无效");
                File.Delete(part);
                HasSavedDownload = false;
                offset = 0;
                SetTransferStatus("下载源无法继续上次进度，正在重新下载");
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
                throw new InvalidDataException("更新源未返回 ZIP 安装包");

            long total = partial ? range!.Length!.Value : response.Content.Headers.ContentLength ?? size;
            if (!partial && offset > 0)
            {
                File.Delete(part);
                offset = 0;
                HasSavedDownload = false;
                SetTransferStatus("下载源未接受断点，正在重新下载");
            }
            if (partial) SetTransferStatus($"继续下载，已保留 {offset / 1_000_000d:0.0} MB");
            bool persist = partial || Math.Max(size, total) >= ResumeThreshold;
            var path = persist ? part : zipPath;
            long received = offset;
            int lastPercent = -1;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, partial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                var buffer = new byte[65536];
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var sampledAt = Stopwatch.GetTimestamp();
                long sampledBytes = offset;
                ReportTransfer(received, total, null);
                while (true)
                {
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await input.ReadAsync(buffer, idleTimeout.Token);
                    idleTimeout.CancelAfter(Timeout.Infinite);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    received += count;
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = Stopwatch.GetElapsedTime(sampledAt, now);
                    if (elapsed >= TimeSpan.FromSeconds(1))
                    {
                        ReportTransfer(received, total, (received - sampledBytes) / elapsed.TotalSeconds);
                        sampledAt = now;
                        sampledBytes = received;
                    }
                    if (total > 0)
                    {
                        int percent = (int)(Math.Min(1, (double)received / total) * 85);
                        if (percent != lastPercent) progress?.Report(percent / 100d);
                        lastPercent = percent;
                    }
                }
                if (total > 0 && received != total)
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "下载中断");
            }
            finally
            {
                TransferProgress = null;
                // Chunked responses can cross the threshold before their final size is known.
                if (!persist && total <= 0 && received >= ResumeThreshold)
                {
                    File.Move(path, part, true);
                    persist = true;
                }
                HasSavedDownload = persist && received > 0;
            }
            if (persist) File.Move(part, zipPath, true);
            HasSavedDownload = false;
            return;
        }
    }

    private void ReportTransfer(long received, long total, double? bytesPerSecond)
    {
        TransferStatus = null;
        var transfer = new UpdateTransferProgress(received, total, bytesPerSecond);
        TransferProgress = transfer;
        TransferProgressChanged?.Invoke(transfer);
    }
}
