using System.Text;
using ASPAssistant.Core.Services;
using FluentAssertions;

namespace ASPAssistant.Tests.Services;

[Collection("Update environment")]
public sealed class AdbOutputEncodingTests
{
    [Fact]
    public void AdbChildStartsOutsideTheInstallation()
    {
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var marker = $"working-dir-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(AdbGameScreens.WorkingDirectory, marker);
        File.WriteAllText(path, marker, Encoding.ASCII);
        try
        {
            AdbGameScreens.Run(shell, ["/d", "/c", "type", marker]).Should().Be(marker);
        }
        finally { File.Delete(path); }
        var installation = Path.Combine(Path.GetTempPath(),"app");
        AdbGameScreens.ChooseWorkingDirectory(installation,Path.Combine(installation,"temp"))
            .Should().Be(Path.GetDirectoryName(installation));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8AdbOutputIsPreservedWithLegacyWindowsConsoleEncoding(bool failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "adb-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Console.OutputEncoding;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.GetEncoding(936);
            const string message = "cannot connect to 127.0.0.1:16384: 由于目标计算机积极拒绝，无法连接。(10061)";
            File.WriteAllText(Path.Combine(directory, "message.txt"), message, new UTF8Encoding(false));
            var command = Path.Combine(directory, "fake-adb.cmd");
            File.WriteAllText(command, "@echo off\r\ntype \"%~dp0message.txt\"" + (failure ? " 1>&2\r\nexit /b 1\r\n" : "\r\n"), Encoding.ASCII);
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (failure)
            {
                Action run = () => AdbGameScreens.Run(executable, ["/d", "/c", command]);
                run.Should().Throw<IOException>().WithMessage("ADB 查询失败：" + message);
            }
            else AdbGameScreens.Run(executable, ["/d", "/c", command]).Should().Be(message);
        }
        finally
        {
            Console.OutputEncoding = previous;
            Directory.Delete(directory, true);
        }
    }
}
