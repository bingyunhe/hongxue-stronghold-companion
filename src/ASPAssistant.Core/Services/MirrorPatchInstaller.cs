using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ASPAssistant.Core.Services;

internal static class MirrorPatchInstaller
{
    private const string InventoryName = "mirror-files.json";
    private sealed record Inventory(string Version, Dictionary<string, string> Files, string[] Directories);

    internal static void Restore(string archive, string installed, string output, string tag, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var inventoryEntry = zip.GetEntry(InventoryName) ?? throw new InvalidDataException("增量包缺少目标文件清单");
        using var inventoryStream = inventoryEntry.Open();
        var inventory = JsonSerializer.Deserialize<Inventory>(inventoryStream);
        if (inventory?.Version != tag || inventory.Files is null || inventory.Directories is null)
            throw new InvalidDataException("增量包目标版本或文件清单不匹配");

        using var changeStream = zip.GetEntry("changes.json")?.Open() ?? throw new InvalidDataException("增量包缺少 changes.json");
        var changes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(changeStream)
            ?? throw new InvalidDataException("增量包变更清单无效");
        var payload = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (operation, names) in changes)
        {
            if (names is null || operation is not ("added" or "modified" or "deleted" or "added_dir" or "deleted_dir"))
                throw new InvalidDataException("增量包变更类型无效");
            foreach (var name in names)
            {
                if (name is null) throw new InvalidDataException("增量包路径为空");
                var path = DeltaInstaller.Resolve(output, name);
                if (operation == "added_dir") Directory.CreateDirectory(path);
                if (operation is "added" or "modified")
                {
                    if (name != InventoryName && !inventory.Files.ContainsKey(name))
                        throw new InvalidDataException($"增量文件不在目标清单：{name}");
                    payload.Add(name);
                }
            }
        }

        // Construct only the target tree. Deleted paths and local extras are never read or removed in the live installation.
        foreach (var name in inventory.Directories)
        {
            if (name is null) throw new InvalidDataException("目标目录名为空");
            Directory.CreateDirectory(DeltaInstaller.Resolve(output, name));
        }
        foreach (var (name, hash) in inventory.Files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = DeltaInstaller.Resolve(output, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = payload.Contains(name)
                ? zip.GetEntry(name)?.Open() ?? throw new InvalidDataException($"增量包缺少文件：{name}")
                : File.OpenRead(DeltaInstaller.Resolve(installed, name));
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            using var sha = SHA256.Create();
            using var verified = new CryptoStream(file, sha, CryptoStreamMode.Write);
            source.CopyTo(verified);
            verified.FlushFinalBlock();
            if (!UpdateService.ChecksumMatches(hash, Convert.ToHexString(sha.Hash!)))
                throw new InvalidDataException($"还原文件校验失败：{name}");
        }
        inventoryEntry.ExtractToFile(Path.Combine(output, InventoryName));
    }
}
