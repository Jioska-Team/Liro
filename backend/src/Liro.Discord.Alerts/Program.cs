using Discord;
using Discord.WebSocket;
using Liro.Application.Alerts;
using Liro.Infrastructure.Alerts;
using Liro.Discord.Alerts.Configuration;
using Liro.Discord.Alerts.Discord;
using Liro.Discord.Alerts.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
var options = new AlertOptions();
builder.Configuration.GetSection("Alerts").Bind(options);
try
{
    var directory = options.ResolveStateDirectory(builder.Environment.EnvironmentName);
    using var instance = new AlertInstance(directory);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new AlertStateDirectory(directory));
    builder.Services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        { GatewayIntents = GatewayIntents.Guilds, LogLevel = LogSeverity.Critical }));
    builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 512 * 1024 });
    builder.Services.AddSingleton<IBandwidthUsageSource, WebshareUsageClient>();
    builder.Services.AddSingleton<IBandwidthAlertDelivery, DiscordBandwidthTransport>();
    builder.Services.AddSingleton(new BandwidthAlertStore(new JsonBandwidthAlertStateStore(directory, options.ChannelId), options.ChannelId));
    builder.Services.AddHostedService<AlertService>();
    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception)
{
    // Provider exceptions can contain request data. Never print tokens or response bodies.
    Console.Error.WriteLine("Liro.Discord.Alerts stopped: verify configuration, state-directory access and bot identity. See bandwidth-health.json for monitoring failures.");
    return 1;
}
