using Discord.Net;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Security;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class SetupTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Token paste preserves content and rejects invalid input", () =>
        {
            if (ConsoleTokenReader.Normalize("  synthetic.token-value_123  ") != "synthetic.token-value_123") { throw new Exception("Paste changed"); }
            foreach (var value in new string?[] { null, "", "  ", "Bot synthetic", "a\tb", "a\nb", "a\u001bb", "\"synthetic\"" })
            {
                try { ConsoleTokenReader.Normalize(value); }
                catch (SetupFailure) { continue; }
                throw new Exception("Invalid token accepted");
            }
        });
        suite.Test("Discord diagnostics distinguish failures without exposing response secrets", () =>
        {
            foreach (var (status, code, expected) in new[] { (401, 0, "rechazo el token"), (403, 50001, "no tiene acceso"), (403, 50013, "Faltan permisos"), (403, 40333, "Cloudflare"), (404, 10003, "no encontro"), (429, 0, "limito") })
            {
                var error = new HttpException((System.Net.HttpStatusCode)status, null!, (global::Discord.DiscordErrorCode)code, "synthetic-secret-response");
                var message = SafeDiagnostic.Describe(error);
                if (!message.Contains(expected) || !message.Contains(status.ToString()) || message.Contains("synthetic-secret")) { throw new Exception("Unsafe or incorrect diagnosis"); }
            }
            if (SafeDiagnostic.Describe(new Exception("synthetic-secret-response")).Contains("synthetic-secret")) { throw new Exception("Exception leaked"); }
        });
    }
}

