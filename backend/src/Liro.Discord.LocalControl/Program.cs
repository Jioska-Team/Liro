using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Discord;
using Liro.Discord.LocalControl.Hosting;
using Liro.Discord.LocalControl.Http;
using Liro.Discord.LocalControl.Security;
using Liro.Discord.LocalControl.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var directory = AgentOptions.LocalDirectory;
var log = new LocalAgentLog(directory);
try
{
    if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("Se requiere Windows."); }
    if (args.Length != 1 || args[0] is not ("configure-webshare" or "configure" or "configure-owner" or "install-panel" or "run"))
    {
        Console.WriteLine("Uso: Liro.Discord.LocalControl configure | configure-owner | configure-webshare | install-panel | run");
        return 2;
    }
    if (args[0] == "configure-webshare")
    {
        Console.Error.WriteLine("Las alertas ahora se configuran en Liro.Discord.Alerts. Consulta docs/discord-alerts.md.");
        return 2;
    }
    using var instance = new AgentInstance(directory);
    if (args[0] == "configure") { await InteractiveSetup.ConfigureAsync(directory); return 0; }
    var options = AgentOptions.Load(directory);
    if (args[0] == "configure-owner")
    {
        Console.Write("Tu ID de usuario Discord (0 para desactivar acceso al panel): ");
        if (!ulong.TryParse(Console.ReadLine()?.Trim(), out var ownerId)) { throw new SetupFailure("ID de usuario invalido."); }
        options.OwnerUserId = ownerId;
        options.Save(directory);
        Console.WriteLine("Propietario guardado localmente. Ejecuta InstallPanel para actualizar los botones.");
        return 0;
    }
    var credentials = new LocalCredentialStore(directory);
    if (args[0] == "install-panel")
    {
        Console.Write("Publicar o reutilizar el boton en el canal configurado? Escribe PUBLICAR: ");
        if (Console.ReadLine() != "PUBLICAR") { return 0; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await new PanelInstaller(options, credentials, directory).EnsurePanelAsync(deadline.Token);
        log.Write(AgentEvent.PanelInstalled);
        Console.WriteLine("Panel disponible. Inicia el agente con run.");
        return 0;
    }
    if (options.PanelMessageId == 0) { throw new InvalidOperationException("Instala primero el panel de Discord."); }
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
    // Never forward framework/Discord exceptions or request bodies to logs.
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(credentials);
    builder.Services.AddSingleton(log);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ICommandRunner, ProcessRunner>();
    builder.Services.AddSingleton<ILocalRuntime, LocalRuntime>();
    builder.Services.AddSingleton<ILanNetworkResolver, LanNetworkResolver>();
    builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 4096
    });
    builder.Services.AddSingleton<IReadinessProbe, LiroReadinessProbe>();
    builder.Services.AddSingleton<IStartupCoordinator, StartupCoordinator>();
    builder.Services.AddSingleton<InteractionHandler>();
    builder.Services.AddSingleton<IDashboardLoginProvider, DashboardLoginProvider>();
    builder.Services.AddSingleton<DashboardInteractionHandler>();
    builder.Services.AddSingleton<DiscordAgentService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordAgentService>());
    using var host = builder.Build();
    var discordService = host.Services.GetRequiredService<DiscordAgentService>();
    log.Write(AgentEvent.Starting);
    await host.RunAsync();
    log.Write(AgentEvent.Stopped);
    return discordService.HasFailed ? 1 : 0;
}
catch (Exception exception)
{
    try { log.Write(AgentEvent.Failed); } catch (Exception) { }
    Console.Error.WriteLine(SafeDiagnostic.Describe(exception));
    return 1;
}




