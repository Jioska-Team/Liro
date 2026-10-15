using Liro.DevelopmentAgent.Diagnostics;
using Discord;
using Discord.Rest;
using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Security;

namespace Liro.DevelopmentAgent.Discord;

public sealed class PanelInstaller(AgentOptions options, LocalCredentialStore credentials, string localDirectory)
{
    public async Task EnsurePanelAsync(CancellationToken cancellationToken)
    {
        using var client = new DiscordRestClient(new DiscordRestConfig { LogLevel = LogSeverity.Critical });
        await client.LoginAsync(TokenType.Bot, credentials.Read());
        var requests = new RequestOptions { CancelToken = cancellationToken, Timeout = 10000 };
        var application = await client.GetApplicationInfoAsync(requests);
        if (application.Id != options.ApplicationId) { throw new SetupFailure("Token y aplicacion no coinciden."); }
        if (await client.GetChannelAsync(options.ChannelId, requests) is not RestTextChannel channel || channel.GuildId != options.GuildId)
        {
            throw new SetupFailure("El canal configurado no pertenece al servidor autorizado.");
        }
        var buttons = new ComponentBuilder().WithButton("Iniciar / obtener API", InteractionHandler.ButtonId, ButtonStyle.Primary);
        if (options.OwnerUserId != 0) { buttons.WithButton("Mi panel Aspire", DashboardInteractionHandler.ButtonId, ButtonStyle.Secondary); }
        var components = buttons.Build();
        if (options.PanelMessageId != 0)
        {
            var message = await channel.GetMessageAsync(options.PanelMessageId, options: requests);
            if (message is not null)
            {
                if (message.Author.Id != client.CurrentUser.Id) { throw new SetupFailure("El mensaje guardado no pertenece al bot."); }
                if (message is not RestUserMessage editable) { throw new SetupFailure("No se pudo editar el panel existente."); }
                await editable.ModifyAsync(properties =>
                {
                    properties.Content = "Liro: acceso al catalogo de desarrollo por red local. Panel Aspire solo para el propietario configurado.";
                    properties.Components = components;
                    properties.AllowedMentions = AllowedMentions.None;
                }, requests);
                return;
            }
        }

        var panel = await channel.SendMessageAsync("Liro: acceso al catalogo de desarrollo por red local. Solo para el rol autorizado.",
            components: components, allowedMentions: AllowedMentions.None, options: requests);
        options.PanelMessageId = panel.Id;
        options.Save(localDirectory);
    }
}


