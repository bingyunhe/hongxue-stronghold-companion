using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASPAssistant.App.Windows;

internal sealed class MirrorChyanCdkStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "mirrorchyan.cdk");
    public bool HasSavedCdk => File.Exists(_path);

    public async Task<string?> LoadAsync()
    {
        byte[] stored;
        try { stored = await File.ReadAllBytesAsync(_path); }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }

        var decoded = ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(decoded); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    public async Task SaveAsync(string input)
    {
        Directory.CreateDirectory(directory);
        var cdk = input.Trim();
        if (cdk.Length == 0)
        {
            File.Delete(_path);
            return;
        }

        var plaintext = Encoding.UTF8.GetBytes(cdk);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, encrypted);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            File.Delete(temporary);
        }
    }
}
