using System.Reflection;
using Liro.Discord.LocalControl.Discord;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class PrivateReplyExpiryTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var connections in new[] { false, true })
        {
            suite.AsyncTest(connections ? "PrivateReply_ConnectionsExpireAfterTwoMinutes" : "PrivateReply_CompletionExpiresAfterTwoMinutes", async () =>
            {
                using var context = new Context();
                var reply = context.Create();
                await reply.Reply.DeferAsync();
                if (connections)
                    await reply.Reply.ShowConnectionsAsync("API disponible", "Acceso privado de prueba");
                else
                    await reply.Reply.CompleteAsync("Completado");
                Assert(reply.Edits == 1, "Completion was not delivered");
                context.Clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
                Assert(!reply.Deleted.Task.IsCompleted, "Reply expired before its deadline");
                context.Clock.Advance(TimeSpan.FromTicks(1));
                await reply.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            });
        }
        suite.AsyncTest("PrivateReply_UnfinishedResponseExpiresAfterTenMinutes", async () =>
        {
            using var context = new Context();
            var reply = context.Create();
            await reply.Reply.DeferAsync();
            context.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
            Assert(!reply.Deleted.Task.IsCompleted, "Unfinished reply expired early");
            context.Clock.Advance(TimeSpan.FromTicks(1));
            await reply.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await reply.Reply.ShowConnectionsAsync("API disponible", "Acceso tardio");
            await reply.Reply.CompleteAsync("Finalizacion tardia");
            Assert(reply.Edits == 0 && context.Transport.Creates == 0, "Expired reply was updated");
        });
        suite.AsyncTest("PrivateReply_LateCompletionCannotExtendMaximumLifetime", async () =>
        {
            using var context = new Context();
            var reply = context.Create();
            await reply.Reply.DeferAsync();
            context.Clock.Advance(TimeSpan.FromMinutes(9));
            await reply.Reply.ShowConnectionsAsync("API disponible", "Acceso privado de prueba");
            context.Clock.Advance(TimeSpan.FromMinutes(1));
            await reply.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(reply.Edits == 1, "Late completion was not delivered before expiration");
        });
        suite.AsyncTest("PrivateReply_ReplacementRejectsStaleCompletionAndKeepsOwnDeadline", async () =>
        {
            using var context = new Context();
            var old = context.Create();
            await old.Reply.DeferAsync();
            context.Clock.Advance(TimeSpan.FromMinutes(1));
            var latest = context.Create();
            await latest.Reply.DeferAsync();
            await old.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await old.Reply.ShowConnectionsAsync("API antigua", "Acceso antiguo");
            Assert(old.Edits == 0 && context.Transport.Creates == 0, "Replaced reply published a stale connection");
            context.Clock.Advance(TimeSpan.FromMinutes(9));
            Assert(!latest.Deleted.Task.IsCompleted, "Old timer deleted the replacement");
            context.Clock.Advance(TimeSpan.FromMinutes(1));
            await latest.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Response
    {
        public IButtonReply Reply = null!;
        public int Edits;
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Context : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "liro-private-reply-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource stopping = new();
        public ManualClock Clock { get; } = new();
        public Transport Transport { get; } = new();
        private readonly PrivateReplyRegistry registry;
        private readonly ConnectionMessageStore store;
        public Context()
        {
            registry = new(Clock);
            store = new(directory, 1, 2, Transport);
        }
        public Response Create()
        {
            var response = new Response();
            // Exercise the production reply path; only Discord HTTP is replaced with callbacks.
            var type = typeof(DiscordAgentService).GetNestedType("DiscordReply", BindingFlags.NonPublic)!;
            response.Reply = (IButtonReply)Activator.CreateInstance(type, [
                (ulong)1,
                (Func<string, Task>)(_ => Task.CompletedTask),
                (Func<string, Task>)(_ => { response.Edits++; return Task.CompletedTask; }),
                (Func<Task>)(() => { response.Deleted.TrySetResult(); return Task.CompletedTask; }),
                registry, store, false, stopping.Token])!;
            return response;
        }
        public void Dispose()
        {
            stopping.Cancel();
            stopping.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class Transport : IConnectionMessageTransport
    {
        public int Creates;
        public Task<ulong> CreateAsync(string content, CancellationToken token)
        {
            Creates++;
            return Task.FromResult((ulong)3);
        }
        public Task<bool> UpdateAsync(ulong messageId, string content, CancellationToken token) => Task.FromResult(true);
        public Task DeleteAsync(ulong messageId, CancellationToken token) => Task.CompletedTask;
    }
}
