using Discord;
using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Discord;
using Liro.DevelopmentAgent.Hosting;
using Liro.DevelopmentAgent.Windows;
using Liro.RegressionTests;

namespace Liro.DevelopmentAgent.RegressionTests;

internal static class SharedPanelTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Panel muestra solo iniciar y detener con estado integrado", () =>
        {
            var buttons = BackendPanel.Components().Components.OfType<ActionRowComponent>()
                .SelectMany(row => row.Components).OfType<ButtonComponent>().ToArray();
            Check(buttons.Length == 2 && buttons[0].CustomId == InteractionHandler.ButtonId
                && buttons[1].CustomId == InteractionHandler.StopButtonId, "Botones incorrectos");
            foreach (var (state, text) in new[] {
                (StartupStatus.Ready, "🟢"), (StartupStatus.Stopped, "🔴"), (StartupStatus.Restarting, "🟡"),
                (StartupStatus.Stopping, "🟡"), (StartupStatus.Unhealthy, "🟠"), (StartupStatus.Unknown, "⚪") })
                Check(BackendPanel.Content(state).Contains(text) && !BackendPanel.Content(state).Contains("https://"), "Estado público incorrecto");
        });
        suite.AsyncTest("Panel compartido refleja cambios y renueva comprobación sin mensajes nuevos", async () =>
        {
            var clock = new ManualClock(); var coordinator = new Coordinator(); var contents = new List<string>();
            var panel = new BackendPanelUpdater(coordinator, clock, (text, _) => { contents.Add(text); return Task.CompletedTask; });
            await panel.RefreshAsync(default);
            await panel.RefreshAsync(default);
            Check(contents.Count == 1 && contents[0].Contains("🟢"), "Repite cambios innecesarios");
            coordinator.State = StartupStatus.Stopped;
            await panel.RefreshAsync(default);
            Check(contents.Count == 2 && contents[^1].Contains("🔴"), "No refleja parada");
            clock.Advance(TimeSpan.FromSeconds(15));
            await panel.RefreshAsync(default);
            Check(contents.Count == 3 && contents[^1].Contains("Última comprobación"), "No renueva fecha");
            await panel.MarkUnavailableAsync(default);
            Check(contents[^1].Contains("⚪"), "Al cerrar conserva estado confirmado");
        });
        suite.AsyncTest("Panel reintenta edición fallida y serializa comprobaciones", async () =>
        {
            var coordinator = new Coordinator(); var attempts = 0;
            var panel = new BackendPanelUpdater(coordinator, TimeProvider.System, (_, _) =>
            {
                if (++attempts == 1) throw new InvalidOperationException("Fallo simulado");
                return Task.CompletedTask;
            });
            try { await panel.RefreshAsync(default); } catch (InvalidOperationException) { }
            await Task.WhenAll(panel.RefreshAsync(default), panel.RefreshAsync(default));
            Check(attempts == 2, "Edición fallida se dio por publicada o se duplicó");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0;
            var serialized = new BackendPanelUpdater(coordinator, TimeProvider.System, async (_, _) =>
            {
                writes++;
                entered.TrySetResult();
                await release.Task;
            });
            var first = serialized.RefreshAsync(default);
            await entered.Task;
            var second = serialized.RefreshAsync(default);
            Check(!second.IsCompleted && writes == 1, "Ediciones simultáneas");
            release.SetResult();
            await Task.WhenAll(first, second);
            Check(writes == 1, "Repitió publicación concurrente");
        });
        suite.AsyncTest("Comprobación fallida sustituye transición antigua por estado sin confirmar", async () =>
        {
            var coordinator = new Coordinator { State = StartupStatus.Restarting };
            var published = new List<string>();
            var panel = new BackendPanelUpdater(coordinator, TimeProvider.System, (text, _) =>
            {
                published.Add(text); return Task.CompletedTask;
            });
            await panel.RefreshAsync(default);
            coordinator.ThrowStatus = true;
            await panel.RefreshAsync(default);
            Check(published.Count == 2 && published[^1].Contains("⚪") && !published[^1].Contains("reiniciando"), "Conservó transición obsoleta");
        });
        suite.AsyncTest("Inicio integra Aspire solo en respuesta privada del propietario", async () =>
        {
            var options = new AgentOptions { OwnerUserId = 7 };
            var provider = new Dashboard();
            var coordinator = new Coordinator();
            var dashboard = new DashboardInteractionHandler(options, provider);
            var handler = new InteractionHandler(options, coordinator, Log(), dashboard);
            var owner = new Reply();
            await handler.CompleteAsync(InteractionHandler.ButtonId, owner, default, 7);
            Check(owner.Text.Contains("Comprobar API") && owner.Text.Contains("privateTestToken"), "Falta respuesta integrada");
            var other = new Reply();
            await handler.CompleteAsync(InteractionHandler.ButtonId, other, default, 8);
            Check(!other.Text.Contains("privateTestToken") && provider.Calls == 1, "Se consultó acceso de otro usuario");
            await handler.CompleteAsync(InteractionHandler.StopButtonId, owner, default, 7);
            Check(!owner.Text.Contains("privateTestToken") && provider.Calls == 1, "Parada entrega acceso Aspire");
            options.OwnerUserId = 0;
            await handler.CompleteAsync(InteractionHandler.ButtonId, owner, default, 0);
            Check(provider.Calls == 1, "Propietario sin configurar recibe acceso");
        });
        suite.AsyncTest("Fallo de Aspire conserva respuesta de API sin filtrar secretos", async () =>
        {
            var options = new AgentOptions { OwnerUserId = 7 };
            var handler = new InteractionHandler(options, new Coordinator(), Log(),
                new DashboardInteractionHandler(options, new Dashboard { Fail = true }));
            var reply = new Reply();
            await handler.CompleteAsync(InteractionHandler.ButtonId, reply, default, 7);
            Check(reply.Text.Contains("Comprobar API") && reply.Text.Contains("No se pudo")
                && !reply.Text.Contains("privateTestToken"), "Error filtra secreto o elimina API");
        });
        suite.AsyncTest("Acción actualiza panel durante reinicio y al terminar aunque falle una edición", async () =>
        {
            var coordinator = new Coordinator { DeferredRestart = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var handler = new InteractionHandler(new(), coordinator, Log());
            var states = new List<StartupStatus>();
            var reply = new Reply();
            var operation = handler.CompleteAsync(InteractionHandler.ButtonId, reply, default, 7, _ =>
            {
                states.Add(coordinator.State);
                if (states.Count == 1) throw new InvalidOperationException("Discord no disponible");
                return Task.CompletedTask;
            });
            Check(states.SequenceEqual(new[] { StartupStatus.Restarting }) && !operation.IsCompleted, "No publicó transición");
            coordinator.State = StartupStatus.Ready;
            coordinator.DeferredRestart.SetResult(new(StartupStatus.Ready, new Uri("http://192.168.1.2:5205/")));
            await operation;
            Check(states.SequenceEqual(new[] { StartupStatus.Restarting, StartupStatus.Ready }) && reply.Text.Contains("Comprobar API"), "Fallo de edición bloqueó operación");
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static LocalAgentLog Log() => new(Path.Combine(AppContext.BaseDirectory, "test-logs"));
    private sealed class Coordinator : IStartupCoordinator
    {
        public StartupStatus State = StartupStatus.Ready;
        public bool ThrowStatus;
        public TaskCompletionSource<StartupResult>? DeferredRestart;
        public Task<StartupResult> EnsureReadyAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<StartupResult> RestartAsync(CancellationToken token)
        {
            if (DeferredRestart is not null) { State = StartupStatus.Restarting; return DeferredRestart.Task; }
            State = StartupStatus.Ready;
            return Task.FromResult(new StartupResult(State, new Uri("http://192.168.1.2:5205/")));
        }
        public Task<StartupResult> StopAsync(CancellationToken token) { State = StartupStatus.Stopped; return Task.FromResult(new StartupResult(State)); }
        public Task<StartupResult> GetStatusAsync(CancellationToken token) => ThrowStatus
            ? throw new InvalidOperationException("Comprobación fallida") : Task.FromResult(new StartupResult(State));
    }
    private sealed class Dashboard : IDashboardLoginProvider
    {
        public int Calls; public bool Fail;
        public Task<Uri?> GetLoginAsync(CancellationToken token)
        {
            Calls++;
            if (Fail) throw new Exception("privateTestToken");
            return Task.FromResult<Uri?>(new Uri("https://localhost:17172/login?t=privateTestToken"));
        }
    }
    private sealed class Reply : IButtonReply
    {
        public string Text = "";
        public Task DeferAsync() => Task.CompletedTask;
        public Task DenyAsync() => Task.CompletedTask;
        public Task CompleteAsync(string message) { Text = message; return Task.CompletedTask; }
    }
}
