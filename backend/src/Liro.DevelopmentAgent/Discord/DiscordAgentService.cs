using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Security;
using Microsoft.Extensions.Hosting;

namespace Liro.DevelopmentAgent.Discord;

public sealed class DiscordAgentService(AgentOptions options, LocalCredentialStore credentials, InteractionHandler handler, LocalAgentLog log, DashboardInteractionHandler dashboard) : BackgroundService
{
    private readonly DiscordSocketClient client = new(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, LogLevel = LogSeverity.Critical });
    private readonly ConcurrentDictionary<Guid, Task> pending = new();
    private readonly PrivateReplyRegistry replies = new(TimeProvider.System);
    private CancellationToken stopping;
    public bool HasFailed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        client.ButtonExecuted += OnButtonAsync;
        client.Connected += () => { log.Write(AgentEvent.Connected); return Task.CompletedTask; };
        client.Disconnected += _ => { log.Write(AgentEvent.Disconnected); return Task.CompletedTask; };
        try
        {
            await client.LoginAsync(TokenType.Bot, credentials.Read());
            var application = await client.GetApplicationInfoAsync();
            if (application.Id != options.ApplicationId) { throw new InvalidOperationException("El token no corresponde a la aplicacion configurada."); }
            await client.StartAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            HasFailed = true;
            log.Write(AgentEvent.Failed);
            throw;
        }
        finally
        {
            await replies.ClearAsync();
            await client.StopAsync();
            await Task.WhenAll(pending.Values);
            client.Dispose();
        }
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        try
        {
            var request = new ButtonRequest(component.GuildId, component.ChannelId ?? 0, component.Message.Id, component.Data.CustomId,
                component.User is SocketGuildUser user ? user.Roles.Select(role => role.Id).ToArray() : []);
            var reply = new DiscordReply(component, replies, stopping);
            if (pending.Count >= 64) { await reply.DenyAsync(); return; }
            var ownerRequest = request.CustomId == DashboardInteractionHandler.ButtonId;
            if (!ownerRequest && !await handler.AcknowledgeAsync(request, reply)) { return; }
            // Return to the gateway after deferral; startup must not block heartbeat processing.
            var key = Guid.NewGuid();
            var task = ownerRequest
                ? dashboard.HandleAsync(request, component.User.Id, reply, stopping)
                : Task.Run(() => handler.CompleteAsync(request.CustomId, reply, stopping));
            pending[key] = task;
            _ = RemoveWhenCompletedAsync(key, task);
        }
        catch (Exception) { log.Write(AgentEvent.Failed); }
    }

    private async Task RemoveWhenCompletedAsync(Guid key, Task task)
    {
        try { await task; }
        finally { pending.TryRemove(key, out _); }
    }

    private sealed class DiscordReply(SocketMessageComponent component, PrivateReplyRegistry registry, CancellationToken stopping) : IButtonReply
    {
        private PrivateReplyRegistry.Lease? lease;

        public async Task DeferAsync()
        {
            // Component DeferAsync can acknowledge an update to the shared panel.
            // An explicit private response ensures subsequent edits target only this reply.
            await component.RespondAsync("Consultando...", ephemeral: true, allowedMentions: AllowedMentions.None);
            lease = await registry.ReplaceAsync(component.User.Id, () => component.DeleteOriginalResponseAsync());
            _ = registry.ExpireAsync(lease, TimeSpan.FromMinutes(10), stopping);
        }
        public Task DenyAsync() => component.RespondAsync("Acceso no autorizado o agente ocupado.", ephemeral: true, allowedMentions: AllowedMentions.None);
        public async Task CompleteAsync(string message)
        {
            if (lease is null || !registry.IsCurrent(lease)) { return; }
            await component.ModifyOriginalResponseAsync(properties =>
            {
                properties.Content = message;
                properties.AllowedMentions = AllowedMentions.None;
            });
            _ = registry.ExpireAsync(lease, TimeSpan.FromMinutes(2), stopping);
        }
    }
}

