// Built with the Windows .NET Framework compiler so the root entry needs no adjacent runtime files.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly: AssemblyTitle("鸿雪·卫戍助手")]
[assembly: AssemblyProduct("Hongxue Stronghold Companion")]
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyFileVersion("0.0.0.0")]
[assembly: AssemblyInformationalVersion("LOCAL")]

namespace HongxueCompanion.Launcher
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

        [STAThread]
        private static int Main(string[] args)
        {
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
            var executable = Path.Combine(directory, "HongxueCompanion.exe");
            if (!File.Exists(executable))
            {
                return Fail("未找到 app\\HongxueCompanion.exe。请完整解压安装包，不要单独移动启动文件。");
            }
            var arguments = new StringBuilder();
            foreach (var argument in args)
            {
                if (arguments.Length > 0) arguments.Append(' ');
                arguments.Append('"');
                var slashes = 0;
                foreach (var character in argument)
                {
                    if (character == '\\') { slashes++; continue; }
                    arguments.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                    arguments.Append(character);
                    slashes = 0;
                }
                arguments.Append('\\', slashes * 2).Append('"');
            }
            try
            {
                var start = new ProcessStartInfo(executable, arguments.ToString())
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false
                };
                Process process;
#if LEGACY_UPDATER
                process = Process.Start(start);
#else
                try { process = Process.Start(start); }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode != 740) throw;
                    start.UseShellExecute = true;
                    start.Verb = "runas";
                    process = Process.Start(start);
                }
#endif
                using (process)
                {
#if LEGACY_UPDATER
                    // Old updaters observe this process while the real main process publishes startup readiness.
                    process.WaitForExit();
                    return process.ExitCode;
#endif
                }
#if !LEGACY_UPDATER
                return 0;
#endif
            }
            catch (Win32Exception error)
            {
#if !LEGACY_UPDATER
                if (error.NativeErrorCode == 1223) return 1; // The user cancelled the UAC prompt.
#endif
                return Fail("无法启动鸿雪·卫戍助手：\n" + error.Message);
            }
        }

        private static int Fail(string message)
        {
#if LEGACY_UPDATER
            var result = Environment.GetEnvironmentVariable("ASPA_UPDATE_STARTUP_RESULT");
            if (!String.IsNullOrEmpty(result))
            {
                var temporary = Path.GetTempPath();
                if (!result.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) return 1;
                var parts = result.Substring(temporary.Length).Split(Path.DirectorySeparatorChar);
                Guid identifier;
                if (parts.Length != 2 || !parts[0].StartsWith("ASPAssistant-Update-", StringComparison.Ordinal)
                    || parts[1].Length != 47 || !parts[1].StartsWith("startup-", StringComparison.Ordinal)
                    || !parts[1].EndsWith(".status", StringComparison.Ordinal)
                    || !Guid.TryParseExact(parts[1].Substring(8, 32), "N", out identifier)) return 1;
                File.WriteAllText(result + ".tmp", "failed\n" + message, new UTF8Encoding(false));
                File.Move(result + ".tmp", result);
                return 1;
            }
#endif
            MessageBoxW(IntPtr.Zero, message, "鸿雪·卫戍助手", 0x10);
            return 1;
        }
    }
}
