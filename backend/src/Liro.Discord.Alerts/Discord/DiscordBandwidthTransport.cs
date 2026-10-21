using Discord;
using Discord.WebSocket;
using Liro.Application.Alerts;
using Liro.Discord.Alerts.Configuration;
namespace Liro.Discord.Alerts.Discord;

public sealed class DiscordBandwidthTransport(DiscordSocketClient client, AlertOptions options) : IBandwidthAlertDelivery
{
    public async Task SendAsync(PendingBandwidthAlert alert, CancellationToken ct)
    {
        var request = new RequestOptions { CancelToken = ct, Timeout = 10000 };
        if (await client.Rest.GetChannelAsync(options.ChannelId, request) is not ITextChannel channel || channel.GuildId != options.GuildId)
            throw new InvalidOperationException("Unauthorized bandwidth channel.");
        var messages = (await channel.GetMessagesAsync(limit: 100, options: request).FlattenAsync()).ToArray();
        if (messages.Any(m => m.Author.Id == client.CurrentUser.Id && m.Content.EndsWith("-# " + alert.Marker, StringComparison.Ordinal)))
            return;
        if (messages.Length >= 100 && messages.Min(m => m.Timestamp) > alert.CreatedAt.AddSeconds(-5))
            throw new InvalidOperationException("Pending delivery cannot be reconciled within the recent channel history.");
        await channel.SendMessageAsync(alert.Content, allowedMentions: AllowedMentions.None, options: request);
    }
}
