using Liro.Discord.LocalControl.Diagnostics;
using Discord;
using Discord.Rest;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Security;

namespace Liro.Discord.LocalControl.Discord;

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
        var components = BackendPanel.Components();
        if (options.PanelMessageId != 0)
        {
            var message = await channel.GetMessageAsync(options.PanelMessageId, options: requests);
            if (message is not null)
            {
                if (message.Author.Id != client.CurrentUser.Id) { throw new SetupFailure("El mensaje guardado no pertenece al bot."); }
                if (message is not RestUserMessage editable) { throw new SetupFailure("No se pudo editar el panel existente."); }
                await editable.ModifyAsync(properties =>
                {
                    properties.Content = BackendPanel.Content(Hosting.StartupStatus.Unknown);
                    properties.Components = components;
                    properties.AllowedMentions = AllowedMentions.None;
                }, requests);
                return;
            }
        }

        var panel = await channel.SendMessageAsync(BackendPanel.Content(Hosting.StartupStatus.Unknown),
            components: components, allowedMentions: AllowedMentions.None, options: requests);
        options.PanelMessageId = panel.Id;
        options.Save(localDirectory);
    }
}


