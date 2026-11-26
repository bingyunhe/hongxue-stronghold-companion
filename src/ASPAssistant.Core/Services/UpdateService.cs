// Modified for ASP Assistant Regulation on 2026-11-09; see NOTICE.
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ASPAssistant.Core.Services;

public record UpdateInfo(
    string TagName,
    string Version,
    string ReleaseNotes,
    string DownloadUrl,
    string AssetName,
    string? ChecksumsUrl)
{
    public UpdateDelta? Delta { get; init; }
    public long FullSize { get; init; }
    public long MirrorSize { get; init; }
    public bool MirrorIncremental { get; init; }
    public string DownloadSizeText => (FromMirrorChyan, MirrorSize, Delta?.Size, FullSize) switch
    {
        (true, > 0, _, _) => $"预计下载 {MirrorSize / 1_000_000d:0.00} MB（{(MirrorIncremental ? "增量包" : "完整包")}）",
        (true, _, _, > 0) => $"GitHub 完整包参考大小 {FullSize / 1_000_000d:0.00} MB",
        (false, _, long size, _) => $"预计下载 {size / 1_000_000d:0.00} MB（增量包）",
        (false, _, _, > 0) => $"预计下载 {FullSize / 1_000_000d:0.00} MB（完整包）",
        _ => "下载开始后确认大小"
    };
    public bool FromMirrorChyan { get; init; }
}

/// <summary>Thrown when the downloaded package does not match the published SHA-256.</summary>
public class UpdateIntegrityException(string message) : Exception(message);

public partial class UpdateService
{
    /// <summary>
    /// 覆盖更新源的环境变量（仅用于本地验证更新流程，正式用户不设置就走 GitHub）。
    /// 值应为返回 GitHub Release JSON 结构的地址。
    /// </summary>
    public const string FeedOverrideVariable = "ASPA_UPDATE_FEED";

    private const string GitHubReleasesUrl = "https://api.github.com/repos/bingyunhe/hongxue-stronghold-companion/releases";
    public const string DefaultFeedUrl = GitHubReleasesUrl + "/latest";

    private const string PackageSuffix = "-win-x64.zip";
    private const string ChecksumsAssetName = "SHA256SUMS.txt";
    private const string AppExecutableName = "ASPAssistant.App.exe";

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "HongxueStrongholdCompanion-Updater/1.0" } }
    };

    private readonly HttpClient _http;

    public UpdateService() : this(SharedHttp, Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)) { }

    internal UpdateService(HttpClient http, Version currentVersion)
    {
        _http = http;
        CurrentVersion = currentVersion;
    }

    internal string MainDirectory { get; init; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public Version CurrentVersion { get; }

    /// <summary>
    /// 返回 true 表示当前是开发构建（版本号为 0.0.0），不进行更新检查。
    /// </summary>
    public bool IsDevBuild => CurrentVersion is { Major: 0, Minor: 0, Build: 0 };

    /// <summary>最近一次失败的人类可读原因；成功时清空。</summary>
    public string? LastError { get; private set; }
    public string? DownloadNotice { get; private set; }

    /// <summary>已下载并暂存好的更新脚本；点了“立即重启”后才启动它。</summary>
    public string? PendingScriptPath { get; private set; }

    /// <summary>本次更新的暂存目录（唯一），便于失败时提示用户手动处理。</summary>
    public string? PendingStagingDir { get; private set; }

    public string FeedUrl => ResolveFeedUrl(Environment.GetEnvironmentVariable(FeedOverrideVariable));

    /// <summary>返回生效的更新源地址：环境变量优先，其次 GitHub。</summary>
    public static string ResolveFeedUrl(string? overrideUrl)
        => string.IsNullOrWhiteSpace(overrideUrl) ? DefaultFeedUrl : overrideUrl.Trim();

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default, bool hasMirrorCdk = true)
    {
        if (IsDevBuild) return null;
        var feedUrl = FeedUrl;
        if (feedUrl != DefaultFeedUrl) return await CheckGitHubUpdateAsync(feedUrl, ct);

        var (release, _) = await QueryMirrorChyanAsync(null, false, ct);
        var version = TryParseVersion(release?.VersionName);
        if (version is not null && version > CurrentVersion)
        {
            var mirrorSize = hasMirrorCdk && release!.UpdateType is "incremental" or "full" ? release.FileSize.GetValueOrDefault() : 0;
            long referenceSize = 0;
            if (mirrorSize <= 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var reference = await CheckGitHubVersionAsync($"v{version}", timeout.Token);
                ct.ThrowIfCancellationRequested();
                if (reference?.Version == version.ToString()) referenceSize = reference.FullSize;
            }
            LastError = null;
            return new UpdateInfo($"v{version}", version.ToString(), release!.ReleaseNote ?? "", "",
                $"hongxue-stronghold-companion-v{version}-win-x64.zip", null)
            {
                FromMirrorChyan = true, MirrorSize = mirrorSize,
                MirrorIncremental = release.UpdateType == "incremental", FullSize = referenceSize
            };
        }

        var fallback = await CheckGitHubUpdateAsync(feedUrl, ct);
        if (version is not null) LastError = null;
        return fallback;
    }

    private Task<UpdateInfo?> CheckGitHubVersionAsync(string tag, CancellationToken ct) =>
        CheckGitHubUpdateAsync($"{GitHubReleasesUrl}/tags/{Uri.EscapeDataString(tag)}", ct);

    private async Task<UpdateInfo?> CheckGitHubUpdateAsync(string feedUrl, CancellationToken ct)
    {
        LastError = null;
        try
        {
            var release = await _http.GetFromJsonAsync<GitHubRelease>(feedUrl, ct);
            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                LastError = "更新源返回的数据不完整";
                return null;
            }

            var latestVersion = TryParseVersion(release.TagName);
            if (latestVersion is null)
            {
                LastError = $"无法解析版本号 {release.TagName}";
                return null;
            }
            if (latestVersion <= CurrentVersion) return null;

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                LastError = $"发布 {release.TagName} 中没有 {PackageSuffix} 包";
                return null;
            }

            var checksums = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase));

            var patchName = $"delta-v{CurrentVersion.ToString(3)}-to-v{latestVersion}.zip";
            var patch = release.Assets.FirstOrDefault(a => a.Name == patchName && a.Size > 0 && a.Size < asset.Size);
            return new UpdateInfo(
                release.TagName,
                latestVersion.ToString(),
                release.Body ?? string.Empty,
                asset.DownloadUrl,
                asset.Name,
                checksums?.DownloadUrl)
            {
                FullSize = asset.Size,
                Delta = patch is not null && checksums is not null ? new(patch.Name, patch.DownloadUrl, patch.Size) : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LastError = DescribeError(ex);
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            LastError = $"更新源返回的不是有效 JSON：{ex.Message}";
            return null;
        }
    }

    public async Task DownloadAndApplyUpdateAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string? mirrorCdk = null)
    {
        TransferStatus = null;
        TransferProgress = null;
        HasSavedDownload = false;
        try
        {
            await PrepareDownloadAsync(info, progress, ct, mirrorCdk);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TransientDownloadError(ex))
        {
            SetTransferStatus("连接中断，3 秒后自动重试（1/1）");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            SetTransferStatus("正在重新连接更新源（1/1）");
            await PrepareDownloadAsync(info, progress, ct, mirrorCdk);
        }
    }

    private async Task PrepareDownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct, string? mirrorCdk,
        bool fullMirrorOnly = false)
    {
        LastError = null;
        DownloadNotice = null;

        // 每次尝试用独立目录：上一次的 update.ps1 可能仍被 PowerShell 占用，
        // 复用固定目录会让 Directory.Delete 抛 IOException 而整条下载失败。
        CleanupStaleUpdateDirs();
        var tempDir = Directory.CreateTempSubdirectory("ASPAssistant-Update-").FullName;
        bool mirrorPatch = false;
        try
        {
            var zipPath = Path.Combine(tempDir, Path.GetFileName(info.AssetName));
            string? expectedHash = null;
            if (info.FromMirrorChyan)
            {
                var release = await TryDownloadMirrorChyanAsync(info, mirrorCdk, zipPath, fullMirrorOnly, progress, ct);
                expectedHash = release?.Sha256;
                mirrorPatch = release?.UpdateType == "incremental";
                if (expectedHash is null)
                {
                    SetTransferStatus($"{DownloadNotice}，正在检查 GitHub 备用下载");
                    var fallback = await CheckGitHubVersionAsync(info.TagName, ct);
                    ct.ThrowIfCancellationRequested();
                    if (fallback is null || fallback.Version != info.Version)
                        throw new InvalidOperationException($"{DownloadNotice}。GitHub Releases 暂无相同版本的下载包，请稍后重新检查更新。");
                    info = fallback with { Delta = null };
                    zipPath = Path.Combine(tempDir, Path.GetFileName(info.AssetName));
                }
            }

            var delta = expectedHash is null ? info.Delta : null;
            if (delta is not null) zipPath = Path.Combine(tempDir, delta.Name);
            var selectedName = delta?.Name ?? info.AssetName;
            if (expectedHash is null)
            {
                if (string.IsNullOrWhiteSpace(info.ChecksumsUrl))
                    throw IntegrityFailure($"发布 {info.TagName} 缺少 {ChecksumsAssetName}，已停止更新");
                var checksumsText = await _http.GetStringAsync(info.ChecksumsUrl, ct);
                expectedHash = ParseChecksum(checksumsText, selectedName)
                    ?? throw IntegrityFailure($"校验文件中没有 {selectedName} 的记录，已停止更新");
                await DownloadPackageAsync(new Uri(delta?.Url ?? info.DownloadUrl), zipPath, info.Version, expectedHash, delta?.Size ?? info.FullSize, progress, ct);
            }

            progress?.Report(0.87);
            var actualHash = await ComputeSha256Async(zipPath, ct);
            if (!ChecksumMatches(expectedHash, actualHash))
            {
                File.Delete(zipPath);
                throw IntegrityFailure(
                    $"安装包校验不通过（期望 {expectedHash[..12]}…，实际 {actualHash[..12]}…），已停止更新");
            }

            progress?.Report(0.92);

            // Extract zip
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            var mainDirectory = MainDirectory;
            var appDir = ResolveInstallationDirectory(mainDirectory);
            if (mirrorPatch) await Task.Run(() => MirrorPatchInstaller.Restore(zipPath, appDir, extractDir, info.TagName, ct), ct);
            else if (delta is null) ZipFile.ExtractToDirectory(zipPath, extractDir);
            else await Task.Run(() => DeltaInstaller.Restore(zipPath, appDir, extractDir,
                CurrentVersion.ToString(3), info.Version, ct), ct);

            progress?.Report(0.96);

            // Resolve the source directory inside the zip: release.yml zips publish/win-x64,
            // so the archive usually has a single root folder.
            var subDirs = Directory.GetDirectories(extractDir);
            var sourceDir = subDirs.Length == 1 && Directory.GetFiles(extractDir).Length == 0 ? subDirs[0] : extractDir;
            var newExecutable = ResolvePackageExecutable(sourceDir);

            var parentDir = Path.GetDirectoryName(appDir) ?? appDir;
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var scriptPath = Path.Combine(tempDir, "update.ps1");

            var scriptContent = BuildUpdateScript(
                Environment.ProcessId,
                sourceDir,
                appDir,
                Path.Combine(mainDirectory, "HongxueCompanion.exe"),
                Path.Combine(parentDir, $"ASPAssistant-Staging-{stamp}"),
                Path.Combine(parentDir, $"ASPAssistant-backup-{stamp}"),
                Path.Combine(tempDir, "update.log"),
                newExecutable, mirrorPatch || delta is not null);

            await File.WriteAllTextAsync(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

            // 只记录脚本位置；真正的替换要等用户点“立即重启”后再启动，
            // 否则脚本会在程序仍运行时白等并误报“无法重命名/已回滚”。
            PendingScriptPath = scriptPath;
            PendingStagingDir = Path.Combine(parentDir, $"ASPAssistant-Staging-{stamp}");
            progress?.Report(1.0);
            return;
        }
        catch (Exception ex) when (mirrorPatch && !ct.IsCancellationRequested &&
            ex is InvalidDataException or JsonException or FileNotFoundException or DirectoryNotFoundException or UpdateIntegrityException)
        {
            SetTransferStatus("Mirror酱 增量包无法还原，正在重新请求完整包");
        }
        finally
        {
            if (PendingScriptPath != Path.Combine(tempDir, "update.ps1"))
                Directory.Delete(tempDir, true);
        }
        await PrepareDownloadAsync(info, progress, ct, mirrorCdk, fullMirrorOnly: true);
    }

    // The optional manifest is the package boundary: legacy releases have no layout file.
    internal static string ResolvePackageExecutable(string sourceDir)
    {
        var executable = AppExecutableName;
        var manifest = Path.Combine(sourceDir, "update-layout.json");
        if (File.Exists(manifest))
        {
            var layout = JsonSerializer.Deserialize<PackageLayout>(File.ReadAllText(manifest));
            if (layout is null || layout.FormatVersion != 1 || layout.Executable is not
                ("ASPAssistant.App.exe" or "HongxueCompanion.exe" or "app/HongxueCompanion.exe"))
                throw new InvalidDataException("更新包布局不受支持，请手动下载最新版本");
            executable = layout.Executable;
        }
        if (!File.Exists(Path.Combine(sourceDir, executable)))
            throw new InvalidDataException($"更新包不完整：缺少 {executable}");
        return executable;
    }

    private sealed record PackageLayout(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("executable")] string? Executable);

    internal static string ResolveInstallationDirectory(string mainDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mainDirectory));
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null && File.Exists(Path.Combine(parent, "update-layout.json")))
        {
            var executable = ResolvePackageExecutable(parent);
            if (string.Equals(Path.GetFullPath(Path.Combine(parent, executable)), Path.Combine(directory, "HongxueCompanion.exe"), StringComparison.OrdinalIgnoreCase))
                return parent;
        }
        return directory;
    }

    /// <summary>
    /// 启动已暂存的更新脚本（应在应用即将退出时调用）。
    /// 返回 false 表示没有可用的暂存脚本。
    /// </summary>
    public bool LaunchPendingUpdateScript()
    {
        if (string.IsNullOrWhiteSpace(PendingScriptPath) || !File.Exists(PendingScriptPath))
            return false;

        using var updater = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -File \"{PendingScriptPath}\"",
            WorkingDirectory = Path.GetDirectoryName(PendingScriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (updater is null) return false;
        PendingScriptPath = null;
        return true;
    }

    /// <summary>尽力清理以前遗留的更新临时目录；失败不影响本次更新。</summary>
    private static void CleanupStaleUpdateDirs()
    {
        try
        {
            var root = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(root, "ASPAssistant-Update-*"))
            {
                try
                {
                    // 保留最近一天的任务，避免重试下载时删除仍待重启的更新。
                    if (Directory.GetLastWriteTimeUtc(dir) >= DateTime.UtcNow.AddDays(-1)) continue;
                    Directory.Delete(dir, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 上一次的 update.ps1 可能仍被 PowerShell 占用，留着即可。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private UpdateIntegrityException IntegrityFailure(string message)
    {
        LastError = message;
        return new UpdateIntegrityException(message);
    }

    /// <summary>把异常翻译成用户和日志都能看懂的短句。</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        UpdateIntegrityException => ex.Message,
        TaskCanceledException => "连接更新源超时",
        HttpRequestException http when (int?)http.StatusCode == 403
            => "更新源拒绝了请求（可能是 GitHub 限流）",
        HttpRequestException http when http.StatusCode is not null
            => $"更新源返回 HTTP {(int)http.StatusCode}",
        HttpRequestException => "无法连接更新源（网络受限或代理拦截）",
        HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => "下载中断，请重试",
        IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            => "文件正被其他程序占用（可能上次更新残留），请关闭杀毒/资源管理器预览后重试",
        IOException => $"读写更新文件失败：{ex.Message}",
        _ => ex.Message
    };

    public static Version? TryParseVersion(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var result) ? result : null;
    }

    /// <summary>从 SHA256SUMS 文本中取出指定文件的哈希（兼容 "hash  name" 与 "hash *name"）。</summary>
    public static string? ParseChecksum(string? checksumsText, string assetName)
    {
        if (string.IsNullOrWhiteSpace(checksumsText)) return null;

        var wanted = Path.GetFileName(assetName);
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var match = ChecksumLine().Match(rawLine.Trim().TrimEnd('\r'));
            if (!match.Success) continue;
            var fileName = Path.GetFileName(match.Groups["name"].Value.Trim());
            if (string.Equals(fileName, wanted, StringComparison.OrdinalIgnoreCase))
                return match.Groups["hash"].Value.ToLowerInvariant();
        }
        return null;
    }

    public static bool ChecksumMatches(string expected, string actual)
        => string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 生成更新脚本：等旧进程退出 → 复制到 staging（与安装目录同盘）→ 旧目录改名备份 →
    /// staging 改名为正式目录 → 启动新版；任一步失败回滚并重新启动旧版。
    /// </summary>
    public static string BuildUpdateScript(
        int pid,
        string sourceDir,
        string appDir,
        string exePath,
        string stagingDir,
        string backupDir,
        string logPath,
        string newExecutableRelativePath, bool incremental = false)
    {
        static string Q(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$appPid = {pid}");
        sb.AppendLine($"$sourceDir = {Q(sourceDir)}");
        sb.AppendLine($"$appDir = {Q(appDir)}");
        sb.AppendLine($"$stagingDir = {Q(stagingDir)}");
        sb.AppendLine($"$backupDir = {Q(backupDir)}");
        sb.AppendLine($"$exePath = {Q(exePath)}");
        sb.AppendLine($"$oldExecutable = {Q(Path.GetRelativePath(appDir, exePath))}");
        sb.AppendLine($"$newExecutable = {Q(newExecutableRelativePath)}");
        sb.AppendLine($"$logPath = {Q(logPath)}");
        sb.AppendLine("$silent = ($env:ASPA_UPDATE_SILENT -eq '1')");
        sb.AppendLine();
        sb.AppendLine("function Write-Log([string]$message) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        $line = \"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message\"");
        sb.AppendLine("        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8");
        sb.AppendLine("    } catch { }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Show-Failure([string]$message) {");
        sb.AppendLine("    Write-Log \"FAILED: $message\"");
        sb.AppendLine("    if ($silent) { return }");
        sb.AppendLine("    try {");
        sb.AppendLine("        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue");
        sb.AppendLine("        [System.Windows.Forms.MessageBox]::Show($message, '鸿雪·卫戍助手更新失败') | Out-Null");
        sb.AppendLine("    } catch { }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Start-Version([string]$path, [string]$resultPath) {");
        sb.AppendLine("    $start = New-Object System.Diagnostics.ProcessStartInfo");
        sb.AppendLine("    $start.FileName = $path");
        sb.AppendLine("    $start.WorkingDirectory = Split-Path -Parent $path");
        sb.AppendLine("    $start.UseShellExecute = $false");
        sb.AppendLine("    $start.EnvironmentVariables.Remove('ASPA_UPDATE_STARTUP_RESULT')");
        sb.AppendLine("    if ($resultPath) { $start.EnvironmentVariables['ASPA_UPDATE_STARTUP_RESULT'] = $resultPath }");
        sb.AppendLine("    $process = [System.Diagnostics.Process]::Start($start)");
        sb.AppendLine("    if ($null -eq $process) { throw '无法启动辅助程序' }");
        sb.AppendLine("    return $process");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Start-OldVersion {");
        sb.AppendLine("    if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { return }");
        sb.AppendLine("    $oldExe = Join-Path -Path $appDir -ChildPath $oldExecutable");
        sb.AppendLine("    if (Test-Path -LiteralPath $oldExe) {");
        sb.AppendLine("        Write-Log \"starting previous version: $oldExe\"");
        sb.AppendLine("        $oldProcess = Start-Version $oldExe ''");
        sb.AppendLine("        $oldProcess.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Log \"update started (pid=$appPid)\"");
        sb.AppendLine("$renamedOld = $false");
        sb.AppendLine("$newProcess = $null");
        sb.AppendLine("try {");
        sb.AppendLine("    $appDir = [System.IO.Path]::GetFullPath($appDir).TrimEnd('\\')");
        sb.AppendLine("    $stagingDir = [System.IO.Path]::GetFullPath($stagingDir).TrimEnd('\\')");
        sb.AppendLine("    $backupDir = [System.IO.Path]::GetFullPath($backupDir).TrimEnd('\\')");
        sb.AppendLine("    $parentDir = Split-Path -Parent $appDir");
        sb.AppendLine("    if (-not $parentDir -or $appDir -eq $stagingDir -or $appDir -eq $backupDir -or $stagingDir -eq $backupDir -or");
        sb.AppendLine("        (Split-Path -Parent $stagingDir) -ne $parentDir -or (Split-Path -Parent $backupDir) -ne $parentDir) {");
        sb.AppendLine("        throw '更新目录路径不正确，已停止替换'");
        sb.AppendLine("    }");
        // 同时移动 PowerShell 位置和原生工作目录，释放继承的安装目录句柄。
        sb.AppendLine("    [Environment]::CurrentDirectory = Split-Path -Parent $logPath");
        sb.AppendLine("    Set-Location -LiteralPath ([Environment]::CurrentDirectory)");
        // 等待旧进程退出：用户可能点了“立即重启”却还在看托盘，给足 5 分钟。
        sb.AppendLine("    $timeout = 300");
        sb.AppendLine("    $elapsed = 0");
        sb.AppendLine("    while ((Get-Process -Id $appPid -ErrorAction SilentlyContinue) -and ($elapsed -lt $timeout)) {");
        sb.AppendLine("        Start-Sleep -Milliseconds 500");
        sb.AppendLine("        $elapsed += 0.5");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Log \"waited $elapsed s for pid $appPid to exit\"");
        sb.AppendLine("    if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) {");
        sb.AppendLine("        throw '辅助仍在运行，请完全退出辅助（含托盘图标）后重新检查更新。'");
        sb.AppendLine("    }");
        sb.AppendLine("    Start-Sleep -Milliseconds 800");
        sb.AppendLine();
        sb.AppendLine("    if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $newExecutable) -PathType Leaf)) {");
        sb.AppendLine("        throw \"更新包不完整：缺少 $newExecutable\"");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    if ((Test-Path -LiteralPath $stagingDir) -or (Test-Path -LiteralPath $backupDir)) { throw '此更新已执行过，请检查原备份或重新下载' }");
        if (incremental)
        {
            sb.AppendLine("""
                    if ([IO.Path]::GetPathRoot($sourceDir) -eq [IO.Path]::GetPathRoot($stagingDir)) {
                        [IO.Directory]::Move($sourceDir, $stagingDir)
                    } else {
                        Copy-Item -LiteralPath $sourceDir -Destination $stagingDir -Recurse -Force
                    }
                    $recovery = "$backupDir-recover.ps1"
                    $shortcut = "$backupDir-recover.cmd"
                    $pendingMarker = (Split-Path -Leaf $backupDir) + '.pending'
                    [IO.File]::WriteAllText((Join-Path $stagingDir $pendingMarker),'')
                """);
            var recovery = $$"""
                param([int]$Watch)
                $ErrorActionPreference = 'Stop'
                $app = {{Q(appDir)}}
                $backup = {{Q(backupDir)}}
                $log = {{Q(logPath)}}
                trap { Add-Content -LiteralPath $log -Encoding UTF8 -Value ("恢复失败：" + $_.Exception.Message); exit 1 }
                $old = {{Q(Path.GetRelativePath(appDir, exePath))}}
                if ($Watch) { Wait-Process -Id $Watch -ErrorAction SilentlyContinue }
                $stage = {{Q(stagingDir)}}
                $main = Join-Path $app {{Q(newExecutableRelativePath)}}
                $marker = Join-Path $app ((Split-Path -Leaf $backup) + '.pending')
                if ([IO.Directory]::Exists($backup) -and [IO.File]::Exists($marker)) {
                    if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($main)) -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $main }) { return }
                    [IO.Directory]::Move($app,$stage)
                }
                if ([IO.Directory]::Exists($backup) -and -not [IO.Directory]::Exists($app)) {
                    [IO.Directory]::Move($backup,$app)
                    Add-Content -LiteralPath $log -Encoding UTF8 -Value '已恢复原安装目录，正在启动旧版'
                    $start = New-Object System.Diagnostics.ProcessStartInfo
                    $start.FileName = Join-Path $app $old
                    $start.WorkingDirectory = Split-Path $start.FileName
                    $start.UseShellExecute = -not $Watch
                    [Diagnostics.Process]::Start($start).Dispose()
                    Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($PSCommandPath,'cmd')),$PSCommandPath
                } elseif (-not $Watch -and [IO.Directory]::Exists($backup)) {
                    Start-Process explorer.exe -ArgumentList ('"' + $backup + '"')
                }
                """;
            sb.AppendLine($"    [IO.File]::WriteAllText($recovery,{Q(recovery)},[Text.UTF8Encoding]::new($true))");
            sb.AppendLine("""
                    $cmd = '@powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0' + [IO.Path]::GetFileName($recovery) + '"'
                    [IO.File]::WriteAllText($shortcut,$cmd,[Text.Encoding]::ASCII)
                    $watcher = New-Object System.Diagnostics.ProcessStartInfo
                    $watcher.FileName = 'powershell.exe'
                    $watcher.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $recovery + '" -Watch ' + $PID
                    $watcher.UseShellExecute = $false
                    $watcher.CreateNoWindow = $true
                    [Diagnostics.Process]::Start($watcher).Dispose()
                """);
        }
        else
            sb.AppendLine("    Copy-Item -LiteralPath $sourceDir -Destination $stagingDir -Recurse -Force");
        sb.AppendLine("    Write-Log \"staged new files to $stagingDir\"");
        sb.AppendLine();
        sb.AppendLine("    $renamedOld = $false");
        sb.AppendLine("    for ($try = 1; $try -le 10; $try++) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            Rename-Item -LiteralPath $appDir -NewName (Split-Path -Leaf $backupDir) -ErrorAction Stop");
        sb.AppendLine("            $renamedOld = $true");
        sb.AppendLine("            break");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            Write-Log \"rename attempt $try failed: $($_.Exception.Message)\"");
        sb.AppendLine("            Start-Sleep -Milliseconds 500");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    Write-Log \"renamed current install to ${backupDir}: $renamedOld\"");
        sb.AppendLine();
        sb.AppendLine("    if (-not $renamedOld) {");
        sb.AppendLine("""
                    try {
                        $stage = Get-Item -LiteralPath $stagingDir -Force
                        if ($stage.FullName -eq $stagingDir -and $stage.Parent.FullName -eq $parentDir -and
                            -not ($stage.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                            -not (Get-ChildItem -LiteralPath $stage.FullName -Recurse -Force -Attributes ReparsePoint | Select-Object -First 1)) {
                            Remove-Item -LiteralPath $stage.FullName -Recurse -Force -ErrorAction Stop
                            Write-Log 'removed staging directory from this attempt'
                        } else { Write-Log 'staging cleanup skipped: unexpected path or link' }
                    } catch { Write-Log "staging cleanup failed: $($_.Exception.Message)" }
                    throw '安装目录仍被占用，未替换任何旧版文件。请关闭该目录的终端或预览。使用模拟器时，旧的 ADB 后台服务也可能占用目录，请在方便断开连接时退出相关工具并重启 Windows，再尝试更新。'
            """);
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    try {");
        sb.AppendLine("        Rename-Item -LiteralPath $stagingDir -NewName (Split-Path -Leaf $appDir)");
        sb.AppendLine("        Write-Log \"activated new version\"");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Write-Log \"activation failed, rolling back: $($_.Exception.Message)\"");
        sb.AppendLine("        if ($renamedOld) { Rename-Item -LiteralPath $backupDir -NewName (Split-Path -Leaf $appDir) }");
        sb.AppendLine("        $renamedOld = $false");
        sb.AppendLine("        Start-OldVersion");
        sb.AppendLine("        Show-Failure \"无法切换新版本，已回滚到旧版本：$($_.Exception.Message)\"");
        sb.AppendLine("        exit 1");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    Start-Sleep -Milliseconds 400");
        sb.AppendLine("    $startupResult = Join-Path (Split-Path -Parent $logPath) ('startup-' + [Guid]::NewGuid().ToString('N') + '.status')");
        sb.AppendLine("    $newProcess = Start-Version (Join-Path $appDir $newExecutable) $startupResult");
        sb.AppendLine("    $startupTimeout = 60");
        sb.AppendLine("    $deadline = [DateTime]::UtcNow.AddSeconds($startupTimeout)");
        sb.AppendLine("    while ($true) {");
        sb.AppendLine("        if (Test-Path -LiteralPath $startupResult) {");
        sb.AppendLine("            $status = Get-Content -LiteralPath $startupResult -Raw -Encoding UTF8");
        sb.AppendLine("            if ($status -eq 'ready') {");
        sb.AppendLine("                if ($newProcess.HasExited) { throw \"新版在确认就绪时已退出（代码 $($newProcess.ExitCode)）\" }");
        sb.AppendLine("                break");
        sb.AppendLine("            }");
        sb.AppendLine("            if ($status.StartsWith(\"failed`n\")) {");
        sb.AppendLine("                $newProcess.WaitForExit(5000) | Out-Null");
        sb.AppendLine("                throw ('新版启动失败：' + $status.Substring(7))");
        sb.AppendLine("            }");
        sb.AppendLine("            throw '新版启动确认内容无效'");
        sb.AppendLine("        }");
        sb.AppendLine("        if ($newProcess.HasExited) { throw \"新版未确认就绪就已退出（代码 $($newProcess.ExitCode)）\" }");
        sb.AppendLine("        if ([DateTime]::UtcNow -ge $deadline) { throw '等待新版启动就绪超时' }");
        sb.AppendLine("        Start-Sleep -Milliseconds 200");
        sb.AppendLine("    }");
        if (incremental)
            sb.AppendLine("    Remove-Item -LiteralPath (Join-Path $appDir $pendingMarker)");
        sb.AppendLine("    Write-Log 'new version confirmed ready'");
        sb.AppendLine();
        sb.AppendLine("    try {");
        sb.AppendLine("        Remove-Item -LiteralPath $backupDir -Recurse -Force");
        sb.AppendLine("        Write-Log \"removed backup $backupDir\"");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Write-Log \"backup kept at $backupDir ($($_.Exception.Message))\"");
        sb.AppendLine("    }");
        sb.AppendLine("} catch {");
        sb.AppendLine("    $message = $_.Exception.Message");
        sb.AppendLine("    Write-Log $message");
        sb.AppendLine("    if ($renamedOld -and $null -ne $newProcess -and -not $newProcess.HasExited) {");
        sb.AppendLine("        Show-Failure \"更新未确认完成：$message。新版进程仍在运行，未执行回滚；旧版备份保留在 $backupDir，请先检查辅助状态。\"");
        sb.AppendLine("        exit 1");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($renamedOld) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }");
        sb.AppendLine("            Rename-Item -LiteralPath $backupDir -NewName (Split-Path -Leaf $appDir)");
        sb.AppendLine("            $renamedOld = $false");
        sb.AppendLine("            Write-Log 'rolled back to previous version'");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            Write-Log \"ROLLBACK FAILED: $($_.Exception.Message)\"");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    if ($renamedOld) {");
        sb.AppendLine("        Show-Failure \"更新未完成且自动恢复失败：$message。请保留备份目录 $backupDir。\"");
        sb.AppendLine("    } else {");
        sb.AppendLine("        Start-OldVersion");
        sb.AppendLine("        Show-Failure \"更新未完成，旧版本文件已保留：$message\"");
        sb.AppendLine("    }");
        sb.AppendLine("    exit 1");
        sb.AppendLine("}");
        sb.AppendLine("finally {");
        sb.AppendLine("    if ($null -ne $newProcess) { $newProcess.Dispose() }");
        if (incremental)
            sb.AppendLine("    if ($recovery -and !(Test-Path -LiteralPath $backupDir)) { Remove-Item -LiteralPath $shortcut,$recovery -ErrorAction SilentlyContinue }");
        sb.AppendLine("}");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$")]
    private static partial Regex ChecksumLine();

    private record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
        [property: JsonPropertyName("size")] long Size = 0);
}
