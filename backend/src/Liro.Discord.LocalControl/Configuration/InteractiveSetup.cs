using Discord;
using Discord.Rest;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Security;

namespace Liro.Discord.LocalControl.Configuration;

public static class InteractiveSetup
{
    public static async Task ConfigureAsync(string localDirectory)
    {
        if (File.Exists(Path.Combine(localDirectory, "settings.json")) || File.Exists(Path.Combine(localDirectory, "token.dpapi")))
        {
            Console.Write("Ya existe configuracion local. Escribe RECONFIGURAR para reemplazarla: ");
            if (Console.ReadLine() != "RECONFIGURAR") { return; }
        }
        var options = new AgentOptions
        {
            BackendPath = Read("Ruta absoluta de la carpeta backend"),
            ApplicationId = ReadId("Application ID del bot"),
            GuildId = ReadId("ID del servidor Discord"),
            ChannelId = ReadId("ID del canal privado de desarrollo"),
            RoleId = ReadId("ID del rol autorizado")
        };
        var network = Read("Indice de interfaz LAN (vacio para deteccion automatica)", optional: true);
        if (network.Length > 0) { options.InterfaceIndex = int.Parse(network, System.Globalization.CultureInfo.InvariantCulture); }
        var docker = Read("Ruta de Docker Desktop.exe (vacio para deteccion automatica)", optional: true);
        options.DockerDesktopPath = docker.Length > 0 ? docker : null;
        options.Validate();
        Console.Write("Token del bot (entrada oculta): ");
        var token = ConsoleTokenReader.Read();
        Console.WriteLine("Validando token con Discord...");
        using var client = new DiscordRestClient(new DiscordRestConfig { LogLevel = LogSeverity.Critical });
        await client.LoginAsync(TokenType.Bot, token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var application = await client.GetApplicationInfoAsync(new RequestOptions { CancelToken = deadline.Token, Timeout = 10000 });
        if (application.Id != options.ApplicationId)
        {
            throw new SetupFailure("El token pertenece a otra aplicacion. Revisa Application ID. No se reemplazo la configuracion.");
        }
        // Persist the token first; invalid input must not replace valid settings.
        new LocalCredentialStore(localDirectory).Save(token);
        options.Save(localDirectory);
        Console.WriteLine("Configuracion guardada en el perfil de Windows, fuera del repositorio.");
    }

    private static string Read(string prompt, bool optional = false)
    {
        Console.Write(prompt + ": ");
        var value = Console.ReadLine()?.Trim() ?? "";
        if (!optional && value.Length == 0) { throw new InvalidOperationException("Falta un valor obligatorio."); }
        return value;
    }

    private static ulong ReadId(string prompt)
    {
        var value = ulong.Parse(Read(prompt), System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }

}

