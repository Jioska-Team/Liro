using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Liro.Discord.LocalControl.Configuration;

namespace Liro.Discord.LocalControl.Discord;

public sealed class ConnectionTransport(DiscordSocketClient client, AgentOptions options) : IConnectionMessageTransport
{
    private static RequestOptions Requests(CancellationToken token) => new() { CancelToken = token, Timeout = 10000 };

    private async Task<ITextChannel> ChannelAsync(CancellationToken token)
    {
        if (await client.Rest.GetChannelAsync(options.ChannelId, Requests(token)) is not ITextChannel channel
            || channel.GuildId != options.GuildId) throw new InvalidOperationException("Canal no autorizado.");
        return channel;
    }

    private async Task<IUserMessage?> MessageAsync(ulong id, CancellationToken token)
    {
        if (id == options.PanelMessageId) throw new InvalidOperationException("El panel no es un mensaje de conexión.");
        var channel = await ChannelAsync(token);
        IMessage? message;
        try { message = await channel.GetMessageAsync(id, options: Requests(token)); }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound) { return null; }
        if (message is null) return null;
        if (message is not IUserMessage editable || message.Author.Id != client.CurrentUser.Id)
            throw new InvalidOperationException("El mensaje no pertenece al bot.");
        return editable;
    }

    public async Task<ulong> CreateAsync(string content, CancellationToken token)
    {
        var channel = await ChannelAsync(token);
        var message = await channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None, options: Requests(token));
        return message.Id;
    }

    public async Task<bool> UpdateAsync(ulong messageId, string content, CancellationToken token)
    {
        var message = await MessageAsync(messageId, token);
        if (message is null) return false;
        try
        {
            await message.ModifyAsync(properties =>
            {
                properties.Content = content;
                properties.AllowedMentions = AllowedMentions.None;
            }, Requests(token));
            return true;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound) { return false; }
    }

    public async Task DeleteAsync(ulong messageId, CancellationToken token)
    {
        var message = await MessageAsync(messageId, token);
        if (message is null) return;
        try { await message.DeleteAsync(Requests(token)); }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound) { }
    }
}
