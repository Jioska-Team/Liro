using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Security;
using Liro.RegressionTests;

namespace Liro.DevelopmentAgent.RegressionTests;

internal static class ConfigurationTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Configuration rejects missing IDs and relative paths", () =>
        {
            var rejected = false;
            try { new AgentOptions { BackendPath = "relative" }.Validate(); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) { throw new Exception("Unsafe configuration accepted"); }
        });
        suite.Test("Credential store protects token and rejects corrupt ciphertext", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "liro-agent-test-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                var store = new LocalCredentialStore(folder);
                store.Save("synthetic-test-token");
                if (store.Read() != "synthetic-test-token") { throw new Exception("Token round trip failed"); }
                var path = Path.Combine(folder, "token.dpapi");
                if (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-test-token")) { throw new Exception("Plaintext persisted"); }
                File.WriteAllBytes(path, [1, 2, 3]);
                var rejected = false;
                try { store.Read(); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
                if (!rejected) { throw new Exception("Corrupt token accepted"); }
            }
            finally { Directory.Delete(folder, true); }
        });
    }
}
