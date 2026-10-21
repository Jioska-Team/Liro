using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Liro.Application.Alerts;
using Liro.Infrastructure.Alerts;
using Liro.Discord.Alerts.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Liro.Discord.Alerts.Hosting;

public sealed record AlertStateDirectory(string Path);

public sealed class AlertService(DiscordSocketClient client, AlertOptions options,
    IBandwidthUsageSource source, IBandwidthAlertDelivery delivery, BandwidthAlertStore store,
    AlertStateDirectory directory, ILogger<AlertService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        DateTimeOffset? lastSuccess = null;
        try
        {
            await client.LoginAsync(TokenType.Bot, options.BotToken);
            var application = await client.GetApplicationInfoAsync();
            if (application.Id != options.ApplicationId)
                throw new InvalidOperationException("Bot identity mismatch.");
            await client.StartAsync();
            while (!ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMinutes(5);
                try
                {
                    if (client.ConnectionState != ConnectionState.Connected)
                        await HealthAsync("DiscordDisconnected", lastSuccess, null, ct);
                    else
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(TimeSpan.FromSeconds(60));
                        var usage = await source.ReadAsync(options.WebshareApiKey, DateTimeOffset.UtcNow, deadline.Token);
                        await store.ObserveAsync(usage, delivery.SendAsync, deadline.Token);
                        lastSuccess = DateTimeOffset.UtcNow;
                        await HealthAsync("Healthy", lastSuccess, usage, ct);
                        logger.LogInformation("Bandwidth checked successfully.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (WebshareApiException ex)
                {
                    if (ex.RetryAfter > delay)
                        delay = ex.RetryAfter;
                    await SafeHealthAsync(ex.Code, lastSuccess, ct);
                    logger.LogWarning("Bandwidth provider check failed: {Code}", ex.Code);
                }
                catch (Exception)
                {
                    await SafeHealthAsync("CheckOrDeliveryFailed", lastSuccess, ct);
                    logger.LogWarning("Bandwidth check or delivery failed; state retained for retry.");
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            await SafeHealthAsync("StartupFailed", lastSuccess, CancellationToken.None);
            Environment.ExitCode = 1;
            // Let the service supervisor restart us without logging provider exceptions or secrets.
            throw new InvalidOperationException("Discord alerts startup failed; verify bot identity and connectivity.");
        }
        finally
        {
            try
            {
                await client.StopAsync();
            }
            catch (Exception) { }
        }
    }
    private async Task SafeHealthAsync(string status, DateTimeOffset? success, CancellationToken ct)
    {
        try
        {
            await HealthAsync(status, success, null, ct);
        }
        catch (Exception) { logger.LogWarning("Cannot persist bandwidth health status."); }
    }
    private async Task HealthAsync(string status, DateTimeOffset? success, BandwidthSnapshot? usage, CancellationToken ct)
    {
        var path = Path.Combine(directory.Path, "bandwidth-health.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new
        {
            Status = status,
            CheckedAt = DateTimeOffset.UtcNow,
            LastSuccessAt = success,
            Usage = usage
        }), ct);
        File.Move(path + ".tmp", path, true);
    }
}
