using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASPAssistant.Core.Services;

public partial class UpdateService
{
    private const string MirrorChyanFeed =
        "https://mirrorchyan.com/api/resources/hongxue-stronghold-companion/latest?os=win&arch=x64&channel=stable&user_agent=hongxue_stronghold_companion_desktop";

    private async Task<(MirrorRelease? Release, string? Error)> QueryMirrorChyanAsync(string? cdk, bool fullOnly, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var address = fullOnly ? MirrorChyanFeed : $"{MirrorChyanFeed}&current_version=v{CurrentVersion.ToString(3)}";
        if (cdk is not null) address += $"&cdk={Uri.EscapeDataString(cdk)}";
        try
        {
            using var response = await _http.GetAsync(address, timeout.Token);
            var body = await response.Content.ReadFromJsonAsync<MirrorResponse>(cancellationToken: timeout.Token);
            if (response.IsSuccessStatusCode && body is { Code: 0, Release: not null })
                return (body.Release, null);
            return (null, body?.Code switch
            {
                7001 => "MirrorChyan CDK 已过期",
                7002 => "MirrorChyan CDK 无效",
                7003 => "MirrorChyan CDK 今日下载次数已用完",
                7004 => "MirrorChyan CDK 不适用于此应用",
                7005 => "MirrorChyan CDK 已被封禁",
                8009 => "MirrorChyan 测试 CDK 无法下载",
                _ => "MirrorChyan 暂时无法提供更新",
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (cdk is null || !TransientDownloadError(ex)) && ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            return (null, "MirrorChyan 查询失败");
        }
    }

    private async Task<MirrorRelease?> TryDownloadMirrorChyanAsync(UpdateInfo info, string? cdk, string zipPath, bool fullOnly,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cdk))
        {
            DownloadNotice = "未填写 MirrorChyan CDK";
            return null;
        }

        for (int attempt = 0; ; attempt++)
        {
            var (release, error) = await QueryMirrorChyanAsync(cdk, fullOnly, ct);
            if (release is null || TryParseVersion(release.VersionName)?.ToString() != info.Version ||
                release.UpdateType is not ("full" or "incremental") || fullOnly && release.UpdateType != "full" ||
                release.Sha256 is not { Length: 64 } sha256 || !sha256.All(Uri.IsHexDigit) ||
                !Uri.TryCreate(release.Url, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
            {
                DownloadNotice = error ?? "MirrorChyan 暂无此版本的适用下载包";
                return null;
            }

            try
            {
                SetTransferStatus(release.UpdateType == "incremental" ? "正在下载 Mirror酱 增量包" : "正在下载 Mirror酱 完整包");
                await DownloadPackageAsync(address, zipPath, info.Version, sha256, release.FileSize ?? 0, progress, ct);
                return release;
            }
            catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                SetTransferStatus("Mirror酱下载链接已过期，正在获取新地址");
                continue;
            }
            catch (Exception ex) when (release.UpdateType == "incremental" &&
                (ex is InvalidDataException || ex is HttpRequestException { StatusCode: not null } && !TransientDownloadError(ex)))
            {
                fullOnly = true;
                attempt = -1;
                SetTransferStatus("Mirror酱 增量包暂不可用，正在请求完整包");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null && !TransientDownloadError(ex))
            {
                DownloadNotice = "MirrorChyan 下载失败或链接已过期";
                return null;
            }
        }
    }

    private sealed record MirrorResponse(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("data")] MirrorRelease? Release);

    private sealed record MirrorRelease(
        [property: JsonPropertyName("version_name")] string? VersionName,
        [property: JsonPropertyName("release_note")] string? ReleaseNote,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("filesize")] long? FileSize,
        [property: JsonPropertyName("update_type")] string? UpdateType);
}
