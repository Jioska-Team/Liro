using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Hosting;

namespace Liro.Discord.LocalControl.Discord;

public sealed record ButtonRequest(ulong? GuildId, ulong ChannelId, ulong MessageId, string CustomId, IReadOnlyCollection<ulong> RoleIds);
public interface IButtonReply
{
    Task DeferAsync();
    Task DenyAsync();
    Task CompleteAsync(string message);
    Task ShowConnectionsAsync(string sharedMessage, string privateMessage) => CompleteAsync(privateMessage);
    Task ClearConnectionsAsync() => Task.CompletedTask;
}

public sealed class InteractionHandler(AgentOptions options, IStartupCoordinator startup, LocalAgentLog log, DashboardInteractionHandler? dashboard = null)
{
    public const string ButtonId = "liro:development:ensure-ready:v1";

    public const string StopButtonId = "liro:development:stop:v1";
    public const string StatusButtonId = "liro:development:status:v1";
    public static string ConnectionDetails(Uri apiBaseUri) => $"Comprobar API: {new Uri(apiBaseUri, "api/lan/ready")}\nURL base para Angular: `{apiBaseUri}`\nConsultar artículo de ejemplo: {new Uri(apiBaseUri, "api/v1/items/by-asset/97886188")}";

    public bool IsAuthorized(ButtonRequest request)
    {
        var allowed = request.GuildId == options.GuildId && request.ChannelId == options.ChannelId
            && options.PanelMessageId != 0 && request.MessageId == options.PanelMessageId
            && (request.CustomId is ButtonId or StopButtonId or StatusButtonId) && request.RoleIds.Contains(options.RoleId);
        return allowed;
    }

    public async Task<bool> AcknowledgeAsync(ButtonRequest request, IButtonReply reply)
    {
        if (!IsAuthorized(request))
        {
            log.Write(AgentEvent.Rejected);
            await reply.DenyAsync();
            return false;
        }
        await reply.DeferAsync();
        return true;
    }

    public Task CompleteAsync(IButtonReply reply, CancellationToken cancellationToken) => CompleteAsync(ButtonId, reply, cancellationToken);

    public async Task CompleteAsync(string action, IButtonReply reply, CancellationToken cancellationToken,
        ulong userId = 0, Func<CancellationToken, Task>? refreshPanel = null)
    {
        try
        {
            var operation = action switch
            {
                ButtonId => startup.RestartAsync(cancellationToken),
                StopButtonId => startup.StopAsync(cancellationToken),
                StatusButtonId => startup.GetStatusAsync(cancellationToken),
                _ => throw new InvalidOperationException("Accion no reconocida.")
            };
            await RefreshAsync();
            var result = await operation;
            await RefreshAsync();
            var message = result.Status switch
            {
                StartupStatus.Ready => ConnectionDetails(result.ApiBaseUri!),
                StartupStatus.Stopped => "🔴 Backend detenido. Docker Desktop permanece disponible.",
                StartupStatus.Busy => "Ya hay una operacion en curso. Espera a que termine y vuelve a intentarlo.",
                StartupStatus.Starting => "🟡 Backend iniciando; la API todavia no esta lista.",
                StartupStatus.Restarting => "🟡 Backend reiniciando.",
                StartupStatus.Stopping => "🟡 Backend deteniendose.",
                StartupStatus.Unhealthy => "🟠 AppHost activo, pero la API no responde como lista.",
                StartupStatus.Unknown => "No se pudo verificar el estado. Revisa el equipo anfitrion.",
                StartupStatus.NetworkChanged => "La red cambio durante el arranque. Revisa la configuracion LAN y el firewall local.",
                StartupStatus.TimedOut => "Se agoto el tiempo de la operacion. Revisa Docker Desktop y Liro en el equipo anfitrion.",
                StartupStatus.DockerUnavailable => "Docker Desktop no esta disponible. Revisa el motor local.",
                StartupStatus.AppHostUnavailable => "Liro no esta listo o ya existe una instancia incompatible. Revisa el equipo anfitrion.",
                _ => "No se pudo completar la operacion. Revisa la configuracion local del agente, Docker y la regla LAN."
            };
            if (action == ButtonId && result.Status == StartupStatus.Ready)
            {
                var combined = dashboard is null ? message : await dashboard.AppendToStartupAsync(message, userId, cancellationToken);
                await reply.ShowConnectionsAsync(options.ShareAspireInChannel ? combined : message, combined);
                return;
            }
            if (action == StopButtonId && result.Status == StartupStatus.Stopped) { await reply.ClearConnectionsAsync(); }
            await reply.CompleteAsync(message);
        }
        catch (Exception) { log.Write(AgentEvent.Failed); }

        async Task RefreshAsync()
        {
            if (refreshPanel is null) { return; }
            try { await refreshPanel(cancellationToken); }
            catch (Exception) { log.Write(AgentEvent.Failed); }
        }
    }
}

