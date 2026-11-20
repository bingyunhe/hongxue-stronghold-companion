using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Data;

public enum ConfigurationScope { Full, Software, Lineups }
public sealed record ConfigurationImportResult(bool Succeeded, string Message, string? BackupPath);

public static class ConfigurationArchive
{
    private const string PendingFile = "pending-configuration.zip";
    private const int MaximumBytes = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly HashSet<string> SoftwareFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "appearance.json", "assistant-hotkey.json", "automatic-current-run-filters.json", "automatic-purchases.json",
        "automatic-round-enemies.json", "board-overlay.json", "close-behavior.json",
        "custom-operators.json", "deploy-pool.json", "game-connection.json", "game-start.json", "game-theme.json",
        "game-theme-auto.json", "light-theme.json", "multiple-lineups.json", "purchase-recognition-speed.json",
        "recognition-hotkey.json", "side-panel-position.json", "skipped_version.txt", "terminal-level.json"
    };
    private sealed record FileEntry(string Path, string Sha256);
    private sealed record Manifest(int FormatVersion, ConfigurationScope Scope, FileEntry[] Files);
    private sealed record Package(ConfigurationScope Scope, Dictionary<string, byte[]> Files);
    public static bool HasPendingImport(string directory) => File.Exists(Path.Combine(directory, PendingFile));

    public static string ScopeName(ConfigurationScope scope) => scope switch
    {
        ConfigurationScope.Full => "完整配置", ConfigurationScope.Software => "软件配置", _ => "阵容配置"
    };

    public static void Export(string directory, string destination, ConfigurationScope scope)
    {
        var files = Collect(directory, scope);
        if (scope != ConfigurationScope.Software) files.TryAdd("lineups/order/deleted-lineups.json", Encoding.UTF8.GetBytes("[]"));
        foreach (var file in files) ValidateContent(file.Key, file.Value);
        WriteNew(destination, Build(scope, files));
    }

    public static string StageImport(string directory, string archive)
    {
        var package = Read(archive);
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, PendingFile);
        var temporary = pending + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Build(package.Scope, package.Files));
            File.Move(temporary, pending, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return $"{ScopeName(package.Scope)}，{package.Files.Count} 个文件";
    }

    public static ConfigurationImportResult? ApplyPending(string directory)
    {
        var pending = Path.Combine(directory, PendingFile);
        if (!File.Exists(pending)) return null; // Normal startup has no queued import.
        using var gate = new FileStream(Path.Combine(directory, "configuration-import.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string? backup = null;
        try
        {
            var package = Read(pending);
            var before = Collect(directory, ConfigurationScope.Full);
            var backupDirectory = Path.GetDirectoryName(Target(directory, "configuration-backups/backup.zip"))!;
            Directory.CreateDirectory(backupDirectory);
            backup = Path.Combine(backupDirectory, $"导入前完整配置-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
            WriteNew(backup, Build(ConfigurationScope.Full, before));
            var written = new List<string>();
            try
            {
                foreach (var file in package.Files)
                {
                    ReplaceFile(directory, file.Key, file.Value);
                    written.Add(file.Key);
                }
                File.Delete(pending);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    foreach (var path in written.AsEnumerable().Reverse())
                    {
                        if (before.TryGetValue(path, out var bytes)) ReplaceFile(directory, path, bytes);
                        else File.Delete(Target(directory, path));
                    }
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                { throw new AggregateException($"配置导入和自动恢复均未完成，请保留原配置备份：{backup}", ex, rollback); }
                throw;
            }
            return new(true, $"{ScopeName(package.Scope)}已导入，原配置备份位于：{backup}", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppLogger.Error("Configuration", "Import failed; previous configuration retained", ex);
            var rejected = Path.GetDirectoryName(Target(directory, "configuration-backups/rejected.zip"))!;
            Directory.CreateDirectory(rejected);
            File.Move(pending, Path.Combine(rejected, $"未应用配置-{Guid.NewGuid():N}.zip"));
            return new(false, "配置导入失败，已保留原配置。请检查配置包和目录权限。", backup);
        }
    }

    private static Dictionary<string, byte[]> Collect(string directory, ConfigurationScope scope)
    {
        Directory.CreateDirectory(directory);
        foreach (var folder in new[] { directory, Path.Combine(directory, "lineups"), Path.Combine(directory, "lineups", "order") })
        {
            var info = new DirectoryInfo(folder);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("配置目录是链接，已停止归档。");
        }
        var paths = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (Allowed(name, scope)) paths.Add(file);
        }
        if (scope != ConfigurationScope.Software)
        {
            try
            {
                paths.AddRange(Directory.EnumerateFiles(Path.Combine(directory, "lineups"), "*.json"));
                paths.AddRange(Directory.EnumerateFiles(Path.Combine(directory, "lineups", "order"), "*.json")
                    .Where(path => Path.GetFileName(path) is "lineups.json" or "deleted-lineups.json"));
            }
            catch (DirectoryNotFoundException) { /* No saved lineups or manual order yet. */ }
        }
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("配置文件是链接，已停止归档。");
            var info = new FileInfo(path);
            total += info.Length;
            if (info.Length > 8 * 1024 * 1024 || total > MaximumBytes || result.Count >= 2048) throw new InvalidDataException("配置文件过多或超过50 MB。");
            result.Add(Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllBytes(path));
        }
        return result;
    }

    private static bool Allowed(string path, ConfigurationScope scope)
    {
        if (path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || Path.IsPathRooted(path)) return false;
        if (scope != ConfigurationScope.Software && path.StartsWith("lineups/", StringComparison.Ordinal))
        {
            var parts = path.Split('/');
            return parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || parts.Length == 3 && parts[1] == "order" && parts[2] is "lineups.json" or "deleted-lineups.json";
        }
        return scope switch
        {
            ConfigurationScope.Full => SoftwareFiles.Contains(path) || path == "tracking.json",
            ConfigurationScope.Software => SoftwareFiles.Contains(path),
            _ => false
        };
    }

    private static byte[] Build(ConfigurationScope scope, Dictionary<string, byte[]> files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new Manifest(1, scope, files.Select(file => new FileEntry(file.Key, Convert.ToHexString(SHA256.HashData(file.Value)))).ToArray());
            using (var output = zip.CreateEntry("configuration-manifest.json").Open()) JsonSerializer.Serialize(output, manifest);
            foreach (var file in files)
            {
                using var output = zip.CreateEntry("files/" + file.Key).Open();
                output.Write(file.Value);
            }
        }
        return memory.ToArray();
    }

    private static Package Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("配置包超过50 MB。");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 2049 || zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
            throw new InvalidDataException("配置包文件过多或包含重复路径。");
        var header = zip.GetEntry("configuration-manifest.json") ?? throw new InvalidDataException("请选择由助手导出的配置包。");
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(header, 1024 * 1024)) ?? throw new InvalidDataException("配置包清单为空。");
        if (manifest.FormatVersion != 1 || !Enum.IsDefined(manifest.Scope) || manifest.Files is null || manifest.Files.Length + 1 != zip.Entries.Count)
            throw new InvalidDataException("配置包版本或清单无效。");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            if (item is null || item.Path is null || !Allowed(item.Path, manifest.Scope) || !files.TryAdd(item.Path, [])) throw new InvalidDataException("配置包包含越界路径或不属于此范围的文件。");
            var entry = zip.GetEntry("files/" + item.Path) ?? throw new InvalidDataException("配置包缺少清单中的文件。");
            total += entry.Length;
            if (entry.Length > 8 * 1024 * 1024 || total > MaximumBytes) throw new InvalidDataException("配置包解压数据超过限制。");
            var bytes = ReadEntry(entry, 8 * 1024 * 1024);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("配置包校验失败。");
            ValidateContent(item.Path, bytes);
            files[item.Path] = bytes;
        }
        return new(manifest.Scope, files);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("配置包文件超过大小限制。");
        using var input = entry.Open();
        var bytes = new byte[(int)entry.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("配置包文件长度不一致。");
        return bytes;
    }

    private static void ValidateContent(string path, byte[] bytes)
    {
        if (path == "skipped_version.txt")
        {
            if (!Version.TryParse(Encoding.UTF8.GetString(bytes).Trim().TrimStart('v'), out _)) throw new InvalidDataException("忽略版本配置无效。");
            return;
        }
        using var json = JsonDocument.Parse(bytes);
        var kind = json.RootElement.ValueKind;
        if (kind == JsonValueKind.Null && path is not ("assistant-hotkey.json" or "recognition-hotkey.json")) throw new InvalidDataException($"配置内容为空：{path}");
        if (path.StartsWith("lineups/", StringComparison.Ordinal) && !path.StartsWith("lineups/order/", StringComparison.Ordinal))
        {
            var lineup = JsonSerializer.Deserialize<Lineup>(bytes, JsonOptions) ?? throw new InvalidDataException("阵容内容为空。");
            if (lineup.Slots is null) throw new InvalidDataException("阵容成员列表无效。");
            lineup.SklandGuide?.Validate();
            if (lineup.EditorStages is { } stages) SklandGuideStage.ValidateAll(stages);
            if (TerminalUpgradeReminder.DataError(lineup.UpgradeReminders) is { Length: > 0 } error) throw new InvalidDataException(error);
        }
        else if (path.StartsWith("lineups/order/", StringComparison.Ordinal)) _ = JsonSerializer.Deserialize<List<string>>(bytes);
        else if (path is "automatic-current-run-filters.json" or "automatic-purchases.json" or "automatic-round-enemies.json"
            or "board-overlay.json" or "game-theme-auto.json" or "light-theme.json" or "multiple-lineups.json") _ = JsonSerializer.Deserialize<bool>(bytes);
        else if (path == "appearance.json") _ = JsonSerializer.Deserialize<SidebarAppearance>(bytes, JsonOptions);
        else if (path == "game-connection.json") _ = JsonSerializer.Deserialize<GameConnectionSettings>(bytes, JsonOptions);
        else if (path == "game-start.json") _ = JsonSerializer.Deserialize<GameStartSettings>(bytes, JsonOptions);
        else if (path == "side-panel-position.json") _ = JsonSerializer.Deserialize<SidePanelPositionSettings>(bytes, JsonOptions);
        else if (path == "deploy-pool.json") _ = JsonSerializer.Deserialize<DeployPoolState>(bytes, JsonOptions);
        else if (path is "assistant-hotkey.json" or "recognition-hotkey.json") _ = JsonSerializer.Deserialize<AssistantHotkey>(bytes, JsonOptions);
        else if (path is "game-theme.json" or "close-behavior.json" or "purchase-recognition-speed.json") _ = JsonSerializer.Deserialize<string>(bytes);
        else if (path == "custom-operators.json")
        {
            var names = JsonSerializer.Deserialize<List<string>>(bytes)!;
            if (names.Count > 12 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new InvalidDataException("自选干员配置必须为不重复的最多12名干员。");
        }
        else if (path == "tracking.json")
        {
            if (kind == JsonValueKind.Array) _ = JsonSerializer.Deserialize<List<TrackingEntry>>(bytes, JsonOptions);
            else
            {
                var state = JsonSerializer.Deserialize<TrackingState>(bytes, JsonOptions)!;
                if (state.Entries is null || state.Lineups is null || state.PurchasedEquipment is null) throw new InvalidDataException("追踪配置列表无效。");
            }
        }
        else if (kind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new InvalidDataException($"配置格式无效：{path}");
    }

    private static string Target(string directory, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("配置路径越界。");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(target)!); parent is not null && parent.FullName.Length >= root.Length; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("配置目录是链接，导入已停止。");
        return target;
    }

    private static void ReplaceFile(string directory, string relative, byte[] bytes)
    {
        var path = Target(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".importing";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { File.Delete(temporary); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(bytes);
    }
}
