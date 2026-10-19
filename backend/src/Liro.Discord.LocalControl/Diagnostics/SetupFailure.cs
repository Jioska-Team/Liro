using Discord.Net;

namespace Liro.Discord.LocalControl.Diagnostics;

public sealed class SetupFailure(string message) : Exception(message);

public static class SafeDiagnostic
{
    public static string Describe(Exception exception)
    {
        var message = exception switch
        {
            SetupFailure known => known.Message,
            HttpException http => DescribeHttp((int)http.HttpCode, (int?)http.DiscordCode),
            OperationCanceledException => "Se agoto el tiempo de espera. Revisa la conexion con Discord.",
            HttpRequestException => "No se pudo conectar con Discord. Revisa Internet, proxy o VPN.",
            System.Security.Cryptography.CryptographicException => "No se pudo descifrar el token. Ejecuta Configure con el mismo usuario Windows.",
            IOException => "No se pudo acceder a los archivos locales o ya existe un agente en ejecucion.",
            _ => "No se pudo completar la operacion. Revisa la configuracion local. No publiques tokens."
        };
        return message;
    }

    public static string DescribeHttp(int status, int? code)
    {
        var reason = (status, code) switch
        {
            (401, _) => "Discord rechazo el token. Copia el token de Bot y vuelve a ejecutar Configure.",
            (_, 40333) => "Discord/Cloudflare bloqueo la conexion. Revisa proxy, VPN o red.",
            (_, 50001) => "El bot no tiene acceso al servidor o canal. Revisa su invitacion y permisos del canal.",
            (_, 50013) => "Faltan permisos del bot en el canal: Ver canal, Enviar mensajes o Leer historial.",
            (403, _) => "Discord denego el acceso. Revisa los permisos del bot en el servidor y canal.",
            (404, _) => "Discord no encontro el recurso. Revisa los IDs del servidor, canal y mensaje.",
            (429, _) => "Discord limito las solicitudes. Espera antes de volver a intentarlo.",
            _ => "Discord no pudo completar la solicitud."
        };
        var message = $"{reason} HTTP {status}; codigo Discord: {code?.ToString() ?? "no disponible"}.";
        return message;
    }
}
