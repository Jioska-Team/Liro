using System.Security.Cryptography;
using System.Text;

namespace Liro.Discord.LocalControl.Security;

public sealed class LocalCredentialStore(string localDirectory)
{
    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) { throw new InvalidOperationException("El token no puede estar vacio."); }
        Directory.CreateDirectory(localDirectory);
        var plain = Encoding.UTF8.GetBytes(token);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var path = Path.Combine(localDirectory, "token.dpapi");
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string Read()
    {
        var encrypted = File.ReadAllBytes(Path.Combine(localDirectory, "token.dpapi"));
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var token = Encoding.UTF8.GetString(plain);
            return token;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
