using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Hosting;
using Liro.Discord.LocalControl.Security;
using Microsoft.Extensions.Hosting;

namespace Liro.Discord.LocalControl.Discord;

public sealed class DiscordAgentService(AgentOptions options, LocalCredentialStore credentials, InteractionHandler handler, LocalAgentLog log, DashboardInteractionHandler dashboard, IStartupCoordinator startup) : BackgroundService
{
    private readonly DiscordSocketClient client = new(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, LogLevel = LogSeverity.Critical });
    private readonly ConcurrentDictionary<Guid, Task> pending = new();
    private readonly PrivateReplyRegistry replies = new(TimeProvider.System);
    private CancellationToken stopping;
    private BackendPanelUpdater? panel;
    private IUserMessage? panelMessage;
    private ConnectionMessageStore? connections;
    private int connectionRestored;
    private GatewayDiagnostics? gateway;
    public bool HasFailed
    {
        get; private set;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        await using var diagnosticLog = new GatewayDiagnosticLog(AgentOptions.LocalDirectory);
        gateway = new GatewayDiagnostics(TimeProvider.System, diagnosticLog.Write);
        panel = new BackendPanelUpdater(startup, TimeProvider.System, PublishPanelAsync);
        connections = new ConnectionMessageStore(AgentOptions.LocalDirectory, options.ChannelId, options.PanelMessageId,
            new ConnectionTransport(client, options));
        client.ButtonExecuted += OnButtonAsync;
        client.Connected += () => { gateway.Connected(); log.Write(AgentEvent.Connected); return Task.CompletedTask; };
        client.Disconnected += error => { gateway.Disconnected(error); log.Write(AgentEvent.Disconnected); return Task.CompletedTask; };
        client.Ready += () => { gateway.SessionReady(); return Task.CompletedTask; };
        using var monitor = new Timer(_ => gateway.Check(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        try
        {
            await client.LoginAsync(TokenType.Bot, credentials.Read());
            var application = await client.GetApplicationInfoAsync();
            if (application.Id != options.ApplicationId)
            {
                throw new InvalidOperationException("El token no corresponde a la aplicacion configurada.");
            }
            await client.StartAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                if (client.ConnectionState == ConnectionState.Connected)
                {
                    try
                    {
                        await RefreshPanelAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception) { panelMessage = null; log.Write(AgentEvent.PanelCheckFailed); }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
            gateway.Stop();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await panel.MarkUnavailableAsync(deadline.Token);
            }
            catch (Exception) { }
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
            var reply = new DiscordReply(component, replies, connections!, options.ShareAspireInChannel, stopping);
            if (pending.Count >= 64)
            {
                await reply.DenyAsync();
                return;
            }
            var ownerRequest = request.CustomId == DashboardInteractionHandler.ButtonId;
            if (!ownerRequest && !await handler.AcknowledgeAsync(request, reply))
            {
                return;
            }
            // Return to the gateway after deferral; startup must not block heartbeat processing.
            var key = Guid.NewGuid();
            var task = ownerRequest
                ? dashboard.HandleAsync(request, component.User.Id, reply, stopping)
                : Task.Run(() => handler.CompleteAsync(request.CustomId, reply, stopping, component.User.Id, RefreshPanelAsync));
            pending[key] = task;
            _ = RemoveWhenCompletedAsync(key, task);
        }
        catch (Exception) { log.Write(AgentEvent.Failed); }
    }

    private async Task RefreshPanelAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        if (panel is null)
        {
            return;
        }
        var version = connections!.Version;
        try
        {
            await panel.RefreshAsync(deadline.Token);
            if (panel.LastResult is { Status: StartupStatus.Ready, ApiBaseUri: not null } ready
                && Interlocked.CompareExchange(ref connectionRestored, 1, 0) == 0)
            {
                try
                {
                    var message = InteractionHandler.ConnectionDetails(ready.ApiBaseUri);
                    if (options.ShareAspireInChannel)
                        message = await dashboard.AppendToStartupAsync(message, 0, token);
                    await connections.ShowAsync(message, token, version);
                }
                catch (Exception) { Interlocked.Exchange(ref connectionRestored, 0); log.Write(AgentEvent.Failed); }
            }
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { log.Write(AgentEvent.PanelCheckTimedOut); }
        catch (Exception) { log.Write(AgentEvent.PanelCheckFailed); }
        using var fallback = CancellationTokenSource.CreateLinkedTokenSource(token);
        fallback.CancelAfter(TimeSpan.FromSeconds(5));
        await panel.MarkUnavailableAsync(fallback.Token);
    }

    private async Task PublishPanelAsync(string content, CancellationToken token)
    {
        var requests = new RequestOptions { CancelToken = token, Timeout = 10000 };
        if (panelMessage is null)
        {
            if (await client.Rest.GetChannelAsync(options.ChannelId, requests) is not ITextChannel channel || channel.GuildId != options.GuildId)
                throw new InvalidOperationException("Canal de panel no válido.");
            if (await channel.GetMessageAsync(options.PanelMessageId, options: requests) is not IUserMessage message
                || message.Author.Id != client.CurrentUser.Id)
                throw new InvalidOperationException("Mensaje de panel no válido.");
            panelMessage = message;
        }
        try
        {
            await panelMessage.ModifyAsync(properties =>
            {
                properties.Content = content;
                properties.Components = BackendPanel.Components();
                properties.AllowedMentions = AllowedMentions.None;
            }, requests);
            log.Write(AgentEvent.PanelUpdated);
            gateway?.PanelAvailable();
        }
        catch
        {
            panelMessage = null;
            log.Write(AgentEvent.PanelUpdateFailed);
            throw;
        }
    }

    private async Task RemoveWhenCompletedAsync(Guid key, Task task)
    {
        try
        {
            await task;
        }
        finally { pending.TryRemove(key, out _); }
    }

    private sealed class DiscordReply(ulong userId, Func<string, Task> respond, Func<string, Task> modify, Func<Task> delete,
        PrivateReplyRegistry registry, ConnectionMessageStore connections, bool sharedAspire, CancellationToken stopping) : IButtonReply
    {
        public DiscordReply(SocketMessageComponent component, PrivateReplyRegistry registry,
            ConnectionMessageStore connections, bool sharedAspire, CancellationToken stopping)
            : this(component.User.Id,
                message => component.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None),
                message => component.ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = message;
                    properties.AllowedMentions = AllowedMentions.None;
                }),
                () => component.DeleteOriginalResponseAsync(), registry, connections, sharedAspire, stopping) { }

        private PrivateReplyRegistry.Lease? lease;
        private readonly long connectionVersion = connections.Version;

        public async Task DeferAsync()
        {
            // An explicit private response ensures subsequent edits target only this reply.
            await respond("Consultando...");
            lease = await registry.ReplaceAsync(userId, delete);
            _ = registry.ExpireAsync(lease, TimeSpan.FromMinutes(10), stopping);
        }
        public Task DenyAsync() => respond("Acceso no autorizado o agente ocupado.");
        public async Task ShowConnectionsAsync(string sharedMessage, string privateMessage)
        {
            if (lease is null || !registry.IsCurrent(lease))
                return;
            await connections.ShowAsync(sharedMessage, stopping, connectionVersion);
            if (sharedAspire || sharedMessage == privateMessage)
                await delete();
            else
                await CompleteAsync(privateMessage);
        }
        public async Task ClearConnectionsAsync()
        {
            await connections.ClearAsync(stopping);
            await registry.ClearAsync();
        }
        public async Task CompleteAsync(string message)
        {
            if (lease is null || !registry.IsCurrent(lease))
                return;
            await modify(message);
            _ = registry.ExpireAsync(lease, TimeSpan.FromMinutes(2), stopping);
        }
    }
}
