namespace Liro.Discord.Alerts.Configuration;

public sealed class AlertOptions
{
    public ulong ApplicationId
    {
        get; set;
    }
    public ulong GuildId
    {
        get; set;
    }
    public ulong ChannelId
    {
        get; set;
    }
    public string BotToken { get; set; } = "";
    public string WebshareApiKey { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string ResolveStateDirectory(string environment)
    {
        if (ApplicationId == 0 || GuildId == 0 || ChannelId == 0 || string.IsNullOrWhiteSpace(BotToken)
            || string.IsNullOrWhiteSpace(WebshareApiKey) || !Path.IsPathFullyQualified(StateDirectory))
            throw new InvalidOperationException("Configure Alerts: ApplicationId, GuildId, ChannelId, BotToken, WebshareApiKey and an absolute StateDirectory.");
        if (environment is not ("Development" or "Staging" or "Production"))
            throw new InvalidOperationException("Use Development, Staging or Production.");
        return Path.Combine(StateDirectory, environment, ApplicationId.ToString(), ChannelId.ToString());
    }
}
