using System.Text.Json;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Discord;
using Liro.Discord.LocalControl.Windows;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class DashboardTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Dashboard login selects only the active project and loopback HTTPS token", () =>
        {
            var backend = Path.Combine(AppContext.BaseDirectory, "project");
            var project = Path.Combine(backend, "src", "Liro.AppHost", "Liro.AppHost.csproj");
            var login = "https://localhost:17172/login?t=syntheticTokenForTestsOnly";
            string Row(string path, string status, string? url) => JsonSerializer.Serialize(new
            {
                appHostPath = path,
                status,
                dashboardUrl = url
            });
            var valid = Row(project, "running", login);
            if (DashboardLoginProvider.Parse("[" + valid + "]", backend)?.AbsoluteUri != login)
            {
                throw new Exception("Valid project rejected");
            }
            foreach (var row in new[] {
                Row(project, "stopped", login), Row(project + ".other", "running", login), Row(project, "running", null),
                Row(project, "running", "https://example.com/login?t=syntheticTokenForTestsOnly"),
                Row(project, "running", "http://localhost/login?t=syntheticTokenForTestsOnly"),
                Row(project, "running", "https://localhost/login?t=syntheticTokenForTestsOnly&redirect=https://example.com"),
                Row(project, "running", "https://localhost/"), Row(project, "running", "https://user@localhost/login?t=syntheticTokenForTestsOnly") })
            {
                if (DashboardLoginProvider.Parse("[" + row + "]", backend) is not null)
                {
                    throw new Exception("Unsafe login accepted");
                }
            }
            if (DashboardLoginProvider.Parse("[" + valid + "," + valid + "]", backend) is not null)
            {
                throw new Exception("Ambiguous session accepted");
            }
        });
        suite.AsyncTest("Dashboard denies other users and mismatched channel before reading credentials", async () =>
        {
            var options = new AgentOptions { OwnerUserId = 7, GuildId = 1, ChannelId = 2, PanelMessageId = 3 };
            var provider = new Provider();
            var handler = new DashboardInteractionHandler(options, provider);
            var valid = new ButtonRequest(1, 2, 3, DashboardInteractionHandler.ButtonId, []);
            foreach (var request in new[] { valid with { GuildId = null }, valid with { GuildId = 9 }, valid with { ChannelId = 9 }, valid with { MessageId = 9 }, valid with { CustomId = InteractionHandler.ButtonId } })
            {
                var reply = new Reply();
                await handler.HandleAsync(request, 7, reply, default);
                if (!reply.Denied || provider.Calls != 0 || reply.Message.Length > 0)
                {
                    throw new Exception("Unauthorized lookup");
                }
            }
            var other = new Reply();
            await handler.HandleAsync(valid, 8, other, default);
            if (!other.Denied || provider.Calls != 0)
            {
                throw new Exception("Wrong owner accepted");
            }
            options.OwnerUserId = 0;
            await handler.HandleAsync(valid, 0, new Reply(), default);
            if (provider.Calls != 0)
            {
                throw new Exception("Unconfigured owner accepted");
            }
            options.OwnerUserId = 7;
            var owner = new Reply();
            provider.BeforeRead = () => { if (!owner.Deferred) { throw new Exception("Missing acknowledgement"); } };
            await handler.HandleAsync(valid, 7, owner, default);
            if (!owner.Message.Contains("syntheticTokenForTestsOnly") || provider.Calls != 1)
            {
                throw new Exception("Owner login missing");
            }
        });
        suite.AsyncTest("Dashboard errors never forward secret-bearing exception messages", async () =>
        {
            var options = new AgentOptions { OwnerUserId = 7, GuildId = 1, ChannelId = 2, PanelMessageId = 3 };
            var provider = new Provider { Fail = true };
            var handler = new DashboardInteractionHandler(options, provider);
            var reply = new Reply();
            await handler.HandleAsync(new ButtonRequest(1, 2, 3, DashboardInteractionHandler.ButtonId, []), 7, reply, default);
            if (reply.Message.Contains("syntheticTokenForTestsOnly") || !reply.Message.Contains("No se pudo"))
            {
                throw new Exception("Exception leaked");
            }
        });
    }

    private sealed class Provider : IDashboardLoginProvider
    {
        public int Calls;
        public bool Fail;
        public Action? BeforeRead;
        public Task<Uri?> GetLoginAsync(CancellationToken cancellationToken)
        {
            Calls++;
            BeforeRead?.Invoke();
            if (Fail)
            {
                throw new Exception("syntheticTokenForTestsOnly");
            }
            return Task.FromResult<Uri?>(new Uri("https://localhost:17172/login?t=syntheticTokenForTestsOnly"));
        }
    }

    private sealed class Reply : IButtonReply
    {
        public bool Deferred, Denied;
        public string Message = "";
        public Task DeferAsync()
        {
            Deferred = true;
            return Task.CompletedTask;
        }
        public Task DenyAsync()
        {
            Denied = true;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(string message)
        {
            Message = message;
            return Task.CompletedTask;
        }
    }
}
