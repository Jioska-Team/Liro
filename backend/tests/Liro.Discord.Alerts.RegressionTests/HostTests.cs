using Liro.Discord.Alerts.Configuration;
using Liro.Discord.Alerts.Hosting;
using Liro.RegressionTests;
namespace Liro.Discord.Alerts.RegressionTests;

internal static class HostTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Alerts_EnvironmentAndChannelStateAreIsolated", () =>
        {
            var options = new AlertOptions { ApplicationId = 1, GuildId = 2, ChannelId = 3,
                BotToken = "test-token", WebshareApiKey = "test-key", StateDirectory = Path.GetTempPath() };
            var staging = options.ResolveStateDirectory("Staging");
            var production = options.ResolveStateDirectory("Production");
            options.ChannelId = 4;
            var other = options.ResolveStateDirectory("Staging");
            if (staging == production || staging == other) throw new Exception("State isolation failed.");
            return Task.CompletedTask;
        });
        suite.AsyncTest("Alerts_InvalidConfigurationFailsBeforeConnecting", () =>
        {
            var rejected = false;
            try { new AlertOptions().ResolveStateDirectory("Production"); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Missing credentials accepted.");
            return Task.CompletedTask;
        });
        suite.AsyncTest("Alerts_InstanceLeaseRejectsDuplicateAndRecoversAfterDispose", () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "alert-lock-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var first = new AlertInstance(directory))
                {
                    var rejected = false;
                    try { using var duplicate = new AlertInstance(directory); }
                    catch (IOException) { rejected = true; }
                    if (!rejected) throw new Exception("Duplicate instance acquired the same state.");
                }
                using var resumed = new AlertInstance(directory);
            }
            finally { Directory.Delete(directory, true); }
            return Task.CompletedTask;
        });
    }
}
