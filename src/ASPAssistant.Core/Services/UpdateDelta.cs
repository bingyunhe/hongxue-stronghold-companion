using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ASPAssistant.Core.Services;

public record UpdateDelta(string Name, string Url, long Size);

internal static class DeltaInstaller
{
    private sealed record Entry(string Mode, string Hash);
    private sealed record Plan(int Format, string Base, string Target, Dictionary<string,string> Old, Dictionary<string,Entry> Files);

    [DllImport("msdelta.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ApplyDeltaW(long flags, string source, string delta, string target);

    internal static void Restore(string archive, string installed, string output, string from, string to, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var header = zip.GetEntry("plan.json")?.Open() ?? throw new InvalidDataException("补丁缺少文件清单");
        var plan = JsonSerializer.Deserialize<Plan>(header);
        if (plan is not { Format: 1 } || plan.Base != from || plan.Target != to || plan.Old is null || plan.Files is null)
            throw new InvalidDataException("补丁版本或格式不匹配");
        foreach (var (name, hash) in plan.Old)
        {
            ct.ThrowIfCancellationRequested();
            var file = Resolve(installed, name);
            if (!File.Exists(file) || !Matches(file, hash))
                throw new InvalidDataException($"本机文件与旧版不一致：{name}");
        }
        var scratch = archive + ".part";
        try
        {
            foreach (var (name, entry) in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Resolve(output, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (entry.Mode == "copy" || entry.Mode == "delta")
                {
                    if (!plan.Old.ContainsKey(name)) throw new InvalidDataException($"补丁缺少原文件记录：{name}");
                    var original = Resolve(installed, name);
                    if (entry.Mode == "copy") File.Copy(original, dest);
                    else
                    {
                        Payload(zip, name).ExtractToFile(scratch, true);
                        if (!ApplyDeltaW(0, original, scratch, dest))
                            throw new InvalidDataException($"无法还原 {name}（Windows {Marshal.GetLastWin32Error()}）");
                    }
                }
                else if (entry.Mode == "file") Payload(zip, name).ExtractToFile(dest);
                else throw new InvalidDataException($"补丁文件操作无效：{name}");
                if (!Matches(dest, entry.Hash)) throw new InvalidDataException($"还原校验失败：{name}");
            }
        }
        finally { File.Delete(scratch); }
    }

    private static ZipArchiveEntry Payload(ZipArchive zip, string name) =>
        zip.GetEntry("payload/" + name) ?? throw new InvalidDataException($"补丁缺少数据：{name}");

    private static bool Matches(string path, string hash)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase);
    }

    private static string Resolve(string root, string name)
    {
        var path = root;
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"补丁路径无效：{name}");
            path = Path.Combine(path, part);
            if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"补丁路径包含链接：{name}");
        }
        return path;
    }
}
