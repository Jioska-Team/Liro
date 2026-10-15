using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Windows;

namespace Liro.DevelopmentAgent.Discord;

public sealed class DashboardInteractionHandler(AgentOptions options, IDashboardLoginProvider dashboard)
{
    public const string ButtonId = "liro:development:owner-dashboard:v1";

    public async Task HandleAsync(ButtonRequest request, ulong userId, IButtonReply reply, CancellationToken cancellationToken)
    {
        var authorized = options.OwnerUserId != 0 && userId == options.OwnerUserId
            && request.GuildId == options.GuildId && request.ChannelId == options.ChannelId
            && options.PanelMessageId != 0 && request.MessageId == options.PanelMessageId
            && request.CustomId == ButtonId;
        if (!authorized) { await reply.DenyAsync(); return; }
        await reply.DeferAsync();
        try
        {
            var login = await dashboard.GetLoginAsync(cancellationToken);
            var message = login is null
                ? "No se encontro el login activo de este AppHost. Inicia primero la API y espera a que este lista. Si ya esta activa, revisa aspire ps --format Json en tu PC. No compartas su salida."
                : $"Tu panel Aspire: <{login.AbsoluteUri}>\nAbre este enlace desde la PC anfitriona. Incluye el token de la sesion actual; no lo compartas. No necesitas iniciar otro AppHost desde Visual Studio.";
            await reply.CompleteAsync(message);
        }
        catch (Exception)
        {
            // CLI failures can contain login URLs. Never forward exception text.
            await reply.CompleteAsync("No se pudo consultar el login de Aspire. Revisa Aspire CLI en la PC anfitriona. No compartas sus registros.");
        }
    }
}
