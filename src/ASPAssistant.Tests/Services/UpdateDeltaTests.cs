using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

public class UpdateDeltaTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("modified")]
    [InlineData("traversal")]
    [InlineData("wrong-target")]
    public void Restore_ValidatesBothVersionsAndWritesOnlyThePreparedTree(string scenario)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var installed = root.CreateSubdirectory("installed").FullName;
            var output = root.CreateSubdirectory("new").FullName;
            var old = Path.Combine(installed, "app.dll");
            File.WriteAllText(old, scenario == "modified" ? "modified" : "old");
            var target = scenario == "traversal" ? "../app.dll" : "app.dll";
            var zipPath = Path.Combine(root.FullName, "patch.zip");
            string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var plan = zip.CreateEntry("plan.json").Open())
                    JsonSerializer.Serialize(plan, new
                    {
                        Format = 1, Base = "1.9.4", Target = "1.9.5",
                        Old = new Dictionary<string,string> { ["app.dll"] = Hash("old") },
                        Files = new Dictionary<string,object> { [target] = new { Mode = "delta", Hash = Hash("new") } }
                    });
                using var data = zip.CreateEntry("payload/app.dll").Open();
                data.Write(Convert.FromHexString("50413330f0d5a577ba78dd0118239c10000109d0985ef3"));
            }
            Action restore = () => DeltaInstaller.Restore(zipPath, installed, output, "1.9.4",
                scenario == "wrong-target" ? "1.9.6" : "1.9.5", CancellationToken.None);
            if (scenario == "valid")
            {
                restore();
                File.ReadAllText(Path.Combine(output, "app.dll")).Should().Be("new");
            }
            else restore.Should().Throw<InvalidDataException>();
            File.ReadAllText(old).Should().Be(scenario == "modified" ? "modified" : "old");
            File.Exists(Path.Combine(root.FullName, "app.dll")).Should().BeFalse();
        }
        finally { root.Delete(true); }
    }
}
