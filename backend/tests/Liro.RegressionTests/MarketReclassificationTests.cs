using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Liro.RegressionTests;

internal static class MarketReclassificationTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var count in new[] { 0, 120, 121, 241 })
            suite.AsyncTest($"Market reclassification pages population count={count}", async () =>
            {
                using var writes = new SemaphoreSlim(1, 1);
                var probe = new Probe(count, writes);
                using var db = Database(probe);
                var changed = await new MarketStore(db).ReclassifyAsync(probe.AsOf, default, writes);
                Check.That(changed == count && probe.Updated.Count == count && probe.Updated.Distinct().Count() == count,
                    "Classification skipped or duplicated identities");
                Check.That(probe.Reads == count / 120 + 1 && probe.Writes == (count + 119) / 120
                    && writes.CurrentCount == 1, "Unexpected page count or leaked write lock");
            });

        suite.AsyncTest("Market reclassification advances past unchanged batches", async () =>
        {
            using var writes = new SemaphoreSlim(1, 1);
            var probe = new Probe(241, writes) { Unchanged = true };
            using var db = Database(probe);
            var changed = await new MarketStore(db).ReclassifyAsync(probe.AsOf, default, writes);
            Check.That(changed == 0 && probe.Updated.Count == 241 && probe.Updated.Distinct().Count() == 241
                && probe.Reads == 3 && probe.Writes == 3, "Unchanged batch stopped or repeated the scan");
        });
        foreach (var cancel in new[] { false, true })
            suite.AsyncTest($"Market reclassification releases write lock after failure cancel={cancel}", async () =>
            {
                using var writes = new SemaphoreSlim(1, 1);
                var probe = new Probe(121, writes) { FailWrite = !cancel, CancelWrite = cancel };
                using var db = Database(probe);
                if (cancel)
                    await Check.ThrowsAsync<OperationCanceledException>(() => new MarketStore(db).ReclassifyAsync(probe.AsOf, default, writes));
                else
                    await Check.ThrowsAsync<InvalidOperationException>(() => new MarketStore(db).ReclassifyAsync(probe.AsOf, default, writes));
                Check.That(writes.CurrentCount == 1 && probe.Reads == 1 && probe.Writes == 1,
                    "Failed batch retained the write lock or continued classification");
            });

        suite.AsyncTest("Market reclassification reads while another writer owns the gate", async () =>
        {
            using var writes = new SemaphoreSlim(0, 1);
            var probe = new Probe(1, writes) { ExpectReadLock = 0 };
            using var db = Database(probe);
            using var cancel = new CancellationTokenSource();
            var task = new MarketStore(db).ReclassifyAsync(probe.AsOf, cancel.Token, writes);
            try
            {
                await probe.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check.That(probe.Writes == 0 && !task.IsCompleted, "Update bypassed the write gate");
                cancel.Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(() => task);
                Check.That(writes.CurrentCount == 0, "Cancellation released another writer's gate");
            }
            finally { cancel.Cancel(); writes.Release(); }
        });
    }

    private static LiroDbContext Database(Probe probe) => new(new DbContextOptionsBuilder<LiroDbContext>()
        .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
        .AddInterceptors(new DeletionConnectionProbe(), probe).Options);

    private sealed class Probe(int count, SemaphoreSlim gate) : DbCommandInterceptor
    {
        public readonly DateTime AsOf = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        public readonly List<Guid> Updated = [];
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads, Writes;
        public int ExpectReadLock = 1;
        public bool FailWrite, CancelWrite, Unchanged;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Check.That(gate.CurrentCount == ExpectReadLock, "Evidence read holds the write gate");
            Check.That(command.CommandText.Contains("LIMIT") && command.CommandText.Contains("ORDER BY")
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, 120)), "Evidence query is unbounded or unordered");
            var cursor = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid>().ToArray();
            Check.That(Reads == 0 ? cursor.Length == 0 : cursor.Contains(Id(Reads * 120)), "Wrong keyset cursor");
            if (Reads > 0)
                Check.That(Regex.IsMatch(command.CommandText, "\"ItemId\" > @"), "Keyset is not exclusive and ascending");
            Check.That(command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, AsOf.Date.AddDays(-7)))
                && command.Parameters.Cast<DbParameter>().Any(p => Equals(p.Value, AsOf.Date)), "Classification window drifted");
            var table = new DataTable();
            table.Columns.Add("ItemId", typeof(Guid));
            table.Columns.Add("LastSuccessAtUtc", typeof(DateTime));
            table.Columns.Add("CompletedDays", typeof(int));
            table.Columns.Add("WeeklySales", typeof(decimal));
            table.Columns.Add("HasRecent", typeof(bool));
            table.Columns.Add("HasHistory", typeof(bool));
            foreach (var index in Enumerable.Range(Reads * 120 + 1, Math.Min(120, Math.Max(0, count - Reads * 120))))
                table.Rows.Add(Id(index), AsOf.AddHours(-1), 7, 70m, true, true);
            var projection = command.CommandText.Split("FROM", 2)[0];
            var columns = Regex.Matches(projection, "\\w+\\.\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
            for (var i = 0; i < columns.Length; i++)
                table.Columns[columns[i]]!.SetOrdinal(i);
            Reads++;
            ReadEntered.TrySetResult();
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Writes++;
            Check.That(gate.CurrentCount == 0, "Classification writes are not serialized");
            Check.That(command.CommandText.Contains("IS NOT DISTINCT FROM e.last")
                && command.CommandText.Contains("\"LeaseUntilUtc\"<=NOW()")
                && command.CommandText.Contains("IS DISTINCT FROM round(e.daily,6)")
                && !command.CommandText.Contains("NextCheckAtUtc"), "Update lost concurrency or scheduling protections");
            var values = command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToArray();
            var ids = values.OfType<Guid[]>().Single();
            Check.That(ids.Length is > 0 and <= 120 && values.OfType<string[]>().Single().All(x => x == "Alta")
                && values.OfType<decimal?[]>().Single().All(x => x == 10m), "Classification rules or batch bound changed");
            if (FailWrite) throw new InvalidOperationException("Simulated update failure");
            if (CancelWrite) throw new OperationCanceledException("Simulated cancellation");
            Updated.AddRange(ids);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(Unchanged ? 0 : ids.Length));
        }

        private static Guid Id(int index) => Guid.Parse($"{index:x8}-0000-0000-0000-000000000000");
    }
}
