using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SecondBrain.App;

internal sealed class ApiKeyStore(string directory, string provider = "Deepgram")
{
    public string FilePath => Path.Combine(directory, provider.ToLowerInvariant() + "-key.protected");
    public bool Exists => File.Exists(FilePath);
    public string Load()
    {
        if (!Exists) throw new InvalidOperationException(provider + " key is not configured yet. Ask Codex to finish setup.");
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { throw new InvalidOperationException("The " + provider + " key belongs to a different Windows account or is damaged. Import it again."); }
    }
    public void Import(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(source).Trim());
        try
        {
            if (bytes.Length < 20 || bytes.Any(b => b <= 32 || b >= 127)) throw new InvalidOperationException("The key file must contain only the API key.");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(FilePath + ".tmp", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
