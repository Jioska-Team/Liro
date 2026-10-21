using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Security;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class ConfigurationTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Configuration accepts organized scripts without root wrappers", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "script-layout-test-" + Guid.NewGuid());
            var scripts = Path.Combine(folder, "scripts", "development");
            var appHost = Path.Combine(folder, "src", "Liro.AppHost");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(appHost);
            try
            {
                File.WriteAllText(Path.Combine(appHost, "Liro.AppHost.csproj"), "");
                File.WriteAllText(Path.Combine(scripts, "Start-Lan.ps1"), "");
                File.WriteAllText(Path.Combine(scripts, "Get-LanConfiguration.ps1"), "");
                var options = new AgentOptions { BackendPath = folder, ApplicationId = 1, GuildId = 2, ChannelId = 3, RoleId = 4 };
                options.Validate();
                File.Delete(Path.Combine(scripts, "Get-LanConfiguration.ps1"));
                var rejected = false;
                try
                {
                    options.Validate();
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected)
                    throw new Exception("Missing relocated helper accepted");
            }
            finally { Directory.Delete(folder, true); }
        });
        suite.Test("Configuration rejects missing IDs and relative paths", () =>
        {
            var rejected = false;
            try
            {
                new AgentOptions { BackendPath = "relative" }.Validate();
            }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected)
            {
                throw new Exception("Unsafe configuration accepted");
            }
        });
        suite.Test("Credential store protects token and rejects corrupt ciphertext", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "liro-agent-test-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                var store = new LocalCredentialStore(folder);
                store.Save("synthetic-test-token");
                if (store.Read() != "synthetic-test-token")
                {
                    throw new Exception("Token round trip failed");
                }
                var path = Path.Combine(folder, "token.dpapi");
                if (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-test-token"))
                {
                    throw new Exception("Plaintext persisted");
                }
                File.WriteAllBytes(path, [1, 2, 3]);
                var rejected = false;
                try
                {
                    store.Read();
                }
                catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
                if (!rejected)
                {
                    throw new Exception("Corrupt token accepted");
                }
            }
            finally { Directory.Delete(folder, true); }
        });
    }
}
