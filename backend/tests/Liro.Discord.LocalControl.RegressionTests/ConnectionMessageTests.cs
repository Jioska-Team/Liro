using Liro.Discord.LocalControl.Discord;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Hosting;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Windows;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class ConnectionMessageTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Conexión persiste al recargar agente y solo Stop la elimina", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "liro-connection-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var transport = new Transport();
                var first = new ConnectionMessageStore(directory, 1, 2, transport);
                await first.ShowAsync("API y token privado de prueba", default);
                Check(transport.Created == 1 && transport.Deleted == 0, "No creó conexión");
                var disk = File.ReadAllText(Path.Combine(directory, "connection-message.json"));
                Check(!disk.Contains("token") && !disk.Contains("API"), "Persistió datos sensibles localmente");
                var reloaded = new ConnectionMessageStore(directory, 1, 2, transport);
                await reloaded.ShowAsync("Nueva sesión", default);
                Check(transport.Created == 1 && transport.Updated == 1 && transport.Deleted == 0, "Duplicó o borró al reiniciar");
                var oldVersion = reloaded.Version;
                await reloaded.ClearAsync(default);
                await reloaded.ShowAsync("Resultado atrasado", default, oldVersion);
                Check(transport.Deleted == 1 && transport.Created == 1 && !File.Exists(Path.Combine(directory, "connection-message.json")), "Respuesta tardía reapareció después de Stop");
                await reloaded.ShowAsync("Siguiente inicio", default, reloaded.Version);
                Check(transport.Created == 2, "No permite nueva sesión");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        suite.AsyncTest("Conexión recupera mensaje borrado y conserva identificador si falla Stop", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "liro-connection-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var transport = new Transport();
                var store = new ConnectionMessageStore(directory, 1, 2, transport);
                await store.ShowAsync("API", default);
                transport.Exists = false;
                await store.ShowAsync("API nueva", default);
                Check(transport.Created == 2, "No recuperó mensaje borrado");
                transport.FailDelete = true;
                try
                {
                    await store.ClearAsync(default);
                }
                catch (IOException) { }
                Check(File.Exists(Path.Combine(directory, "connection-message.json")), "Perdió identificador tras fallo");
                transport.FailDelete = false;
                await new ConnectionMessageStore(directory, 1, 2, transport).ClearAsync(default);
                Check(transport.Deleted == 1, "No reintentó limpieza");
                var otherChannel = new ConnectionMessageStore(directory, 9, 2, transport);
                await otherChannel.ClearAsync(default);
                Check(transport.Deleted == 1, "Borró un mensaje ajeno");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        suite.AsyncTest("Canal autorizado recibe datos limpios y Aspire solo si se habilitó compartir", async () =>
        {
            var options = new AgentOptions { ShareAspireInChannel = true, OwnerUserId = 7 };
            var provider = new Dashboard();
            var coordinator = new Coordinator();
            var handler = new InteractionHandler(options, coordinator,
                new LocalAgentLog(Path.Combine(AppContext.BaseDirectory, "test-logs")), new DashboardInteractionHandler(options, provider));
            var reply = new Reply();
            await handler.CompleteAsync(InteractionHandler.ButtonId, reply, default, 8);
            Check(reply.Shared.Contains("sessionForTest") && reply.Shared.Contains("/97886188")
                && !reply.Shared.Contains("ASSET_ID") && !reply.Shared.Contains("Backend activo")
                && !reply.Shared.Contains("misma red") && !reply.Shared.Contains("anfitriona"), "Texto de conexión incorrecto");
            options.ShareAspireInChannel = false;
            await handler.CompleteAsync(InteractionHandler.ButtonId, reply, default, 7);
            Check(!reply.Shared.Contains("sessionForTest") && reply.Private.Contains("sessionForTest"), "Filtró enlace privado");
            coordinator.StopStatus = StartupStatus.Busy;
            await handler.CompleteAsync(InteractionHandler.StopButtonId, reply, default, 7);
            Check(reply.Cleared == 0, "Borró sin detener");
            coordinator.StopStatus = StartupStatus.Stopped;
            await handler.CompleteAsync(InteractionHandler.StopButtonId, reply, default, 7);
            Check(reply.Cleared == 1, "No limpia tras Stop confirmado");
        });
    }
    private static void Check(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
    private sealed class Transport : IConnectionMessageTransport
    {
        public int Created, Updated, Deleted; public bool Exists, FailDelete;
        public Task<ulong> CreateAsync(string content, CancellationToken token)
        {
            Created++;
            Exists = true;
            return Task.FromResult((ulong)(10 + Created));
        }
        public Task<bool> UpdateAsync(ulong id, string content, CancellationToken token)
        {
            Updated++;
            return Task.FromResult(Exists);
        }
        public Task DeleteAsync(ulong id, CancellationToken token)
        {
            if (FailDelete)
                throw new IOException();
            Deleted++;
            Exists = false;
            return Task.CompletedTask;
        }
    }
    private sealed class Dashboard : IDashboardLoginProvider
    {
        public Task<Uri?> GetLoginAsync(CancellationToken token) => Task.FromResult<Uri?>(new("https://localhost:17172/login?t=sessionForTest"));
    }
    private sealed class Coordinator : IStartupCoordinator
    {
        public StartupStatus StopStatus = StartupStatus.Stopped;
        public Task<StartupResult> RestartAsync(CancellationToken token) => Task.FromResult(new StartupResult(StartupStatus.Ready, new("http://192.168.1.2:5205/")));
        public Task<StartupResult> StopAsync(CancellationToken token) => Task.FromResult(new StartupResult(StopStatus));
        public Task<StartupResult> EnsureReadyAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<StartupResult> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Reply : IButtonReply
    {
        public string Shared = "", Private = ""; public int Cleared;
        public Task DeferAsync() => Task.CompletedTask;
        public Task DenyAsync() => Task.CompletedTask;
        public Task CompleteAsync(string content) => Task.CompletedTask;
        public Task ShowConnectionsAsync(string shared, string personal)
        {
            Shared = shared;
            Private = personal;
            return Task.CompletedTask;
        }
        public Task ClearConnectionsAsync()
        {
            Cleared++;
            return Task.CompletedTask;
        }
    }
}

