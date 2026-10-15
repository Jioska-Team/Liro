using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Hosting;

namespace Liro.DevelopmentAgent.Discord;

public sealed record ButtonRequest(ulong? GuildId, ulong ChannelId, ulong MessageId, string CustomId, IReadOnlyCollection<ulong> RoleIds);
public interface IButtonReply
{
    Task DeferAsync();
    Task DenyAsync();
    Task CompleteAsync(string message);
}

public sealed class InteractionHandler(AgentOptions options, IStartupCoordinator startup, LocalAgentLog log)
{
    public const string ButtonId = "liro:development:ensure-ready:v1";

    public bool IsAuthorized(ButtonRequest request)
    {
        var allowed = request.GuildId == options.GuildId && request.ChannelId == options.ChannelId
            && options.PanelMessageId != 0 && request.MessageId == options.PanelMessageId
            && request.CustomId == ButtonId && request.RoleIds.Contains(options.RoleId);
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

    public async Task CompleteAsync(IButtonReply reply, CancellationToken cancellationToken)
    {
        try
        {
            var result = await startup.EnsureReadyAsync(cancellationToken);
            var message = result.Status switch
            {
                StartupStatus.Ready => $"Comprobar API: {new Uri(result.ApiBaseUri!, "api/lan/ready")}\nURL base para Angular: `{result.ApiBaseUri}`\nConsultar item: `{new Uri(result.ApiBaseUri!, "api/v1/items/by-asset/")}ASSET_ID`\nAcceso solo desde la misma red local. La raiz / no es una pagina web.",
                StartupStatus.NetworkChanged => "La red cambio durante el arranque. Revisa la configuracion LAN y el firewall local.",
                StartupStatus.TimedOut => "Se agoto el tiempo de arranque. Revisa Docker Desktop y Liro en el equipo anfitrion.",
                StartupStatus.DockerUnavailable => "Docker Desktop no esta disponible. Revisa el motor local.",
                StartupStatus.AppHostUnavailable => "Liro no esta listo o ya existe una instancia incompatible. Revisa el equipo anfitrion.",
                _ => "No se pudo iniciar. Revisa la configuracion local del agente, Docker y la regla LAN."
            };
            await reply.CompleteAsync(message);
        }
        catch (Exception) { log.Write(AgentEvent.Failed); }
    }
}

