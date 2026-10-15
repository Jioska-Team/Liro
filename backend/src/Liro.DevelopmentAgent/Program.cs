using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Discord;
using Liro.DevelopmentAgent.Hosting;
using Liro.DevelopmentAgent.Http;
using Liro.DevelopmentAgent.Security;
using Liro.DevelopmentAgent.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var directory = AgentOptions.LocalDirectory;
var log = new LocalAgentLog(directory);
try
{
    if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("Se requiere Windows."); }
    if (args.Length != 1 || args[0] is not ("configure" or "install-panel" or "run"))
    {
        Console.WriteLine("Uso: Liro.DevelopmentAgent configure | install-panel | run");
        return 2;
    }
    using var instance = new AgentInstance(directory);
    if (args[0] == "configure") { await InteractiveSetup.ConfigureAsync(directory); return 0; }
    var options = AgentOptions.Load(directory);
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


