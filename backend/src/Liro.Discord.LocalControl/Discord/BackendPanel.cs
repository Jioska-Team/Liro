using Discord;
using Liro.Discord.LocalControl.Hosting;

namespace Liro.Discord.LocalControl.Discord;

public static class BackendPanel
{
    public static MessageComponent Components() => new ComponentBuilder()
        .WithButton("Iniciar / reiniciar API", InteractionHandler.ButtonId, ButtonStyle.Primary)
        .WithButton("Stop", InteractionHandler.StopButtonId, ButtonStyle.Danger).Build();

    public static string Content(StartupStatus status) => "Liro: control del backend de desarrollo.\n" + (status switch
    {
        StartupStatus.Ready => "🟢 Backend activo; API lista.",
        StartupStatus.Stopped => "🔴 Backend detenido.",
        StartupStatus.Starting => "🟡 Backend iniciando; esperando a la API.",
        StartupStatus.Restarting => "🟡 Backend reiniciando.",
        StartupStatus.Stopping => "🟡 Backend deteniéndose.",
        StartupStatus.Unhealthy => "🟠 AppHost activo; API no disponible.",
        _ => "⚪ Estado sin confirmar."
    });
}

// One shared message; successful writes alone advance the last published state.
public sealed class BackendPanelUpdater(IStartupCoordinator startup, TimeProvider clock,
    Func<string, CancellationToken, Task> publish)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? lastContent;
    private DateTimeOffset lastPublished;
    public StartupResult? LastResult { get; private set; }

    public async Task RefreshAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            StartupResult result;
            try { result = await startup.GetStatusAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { result = new(StartupStatus.Unknown); }
            await PublishAsync(result.Status, token);
            LastResult = result;
        }
        finally { gate.Release(); }
    }

    public async Task MarkUnavailableAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await PublishAsync(StartupStatus.Unknown, token); }
        finally { gate.Release(); }
    }

    private async Task PublishAsync(StartupStatus status, CancellationToken token)
    {
        var content = BackendPanel.Content(status);
        var now = clock.GetUtcNow();
        if (content == lastContent && now - lastPublished < TimeSpan.FromSeconds(1)) { return; }
        await publish(content + $"\nÚltima comprobación: <t:{now.ToUnixTimeSeconds()}:T>.", token);
        lastContent = content;
        lastPublished = now;
    }
}
