using System.Runtime.InteropServices;
using Liro.DevelopmentAgent.Diagnostics;

namespace Liro.DevelopmentAgent.Security;

public static class ConsoleTokenReader
{
    public static string Read()
    {
        // Borrowed standard-input handle: it must not be closed by this method.
        var handle = GetStdHandle(-10);
        if (Console.IsInputRedirected || GetConsoleMode(handle, out var original) == 0)
        {
            throw new SetupFailure("Abre PowerShell interactivo para introducir el token de forma oculta.");
        }
        // Use native line editing/paste, but disable echo and VT escape input.
        var hiddenMode = (original | 3u) & ~0x0204u;
        if (SetConsoleMode(handle, hiddenMode) == 0)
        {
            throw new SetupFailure("No se pudo ocultar la entrada. No se leyo ni guardo el token.");
        }
        try
        {
            var token = Normalize(Console.ReadLine());
            return token;
        }
        finally
        {
            var restored = SetConsoleMode(handle, original);
            Console.WriteLine();
            if (restored == 0) { throw new SetupFailure("No se pudo restaurar la consola. Cierra esta ventana y abre otra."); }
        }
    }

    public static string Normalize(string? input)
    {
        var token = input?.Trim() ?? "";
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl)
            || token.Contains('"') || token.Contains('\''))
        {
            throw new SetupFailure("Token vacio o con caracteres no validos. Usa Copiar en Bot; pega solo el token, sin comillas ni prefijo Bot.");
        }
        return token;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetConsoleMode(nint handle, uint mode);
}
