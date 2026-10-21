using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Enums;
using Liro.Domain.Catalog.Models;
using Liro.Infrastructure.Persistence;
using Liro.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Liro.RegressionTests;

internal static class CatalogKeyContractTests
{
    public static void Register(RegressionSuite suite)
    {
        foreach (var type in new[] { CatalogItemType.Asset, CatalogItemType.Bundle })
        {
            suite.AsyncTest($"Repository existence query preserves catalog identity type={type}", async () =>
            {
                var key = new CatalogItemKey(126, type);
                var probe = new IdentityCommandProbe(key);
                var options = new DbContextOptionsBuilder<LiroDbContext>()
                    .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
                    .AddInterceptors(new DeletionConnectionProbe(), probe).Options;
                using var db = new LiroDbContext(options);
                IItemRepository repository = new ItemRepository(db);

                probe.Exists = true;
                Check.That(await repository.ExistsByCatalogIdAsync(key), "Existing identity was reported missing");
                probe.Exists = false;
                Check.That(!await repository.ExistsByCatalogIdAsync(key), "Missing identity was reported present");
                Check.That(probe.Reads == 2 && !db.ChangeTracker.Entries().Any(), "Existence check materialized entities or skipped its query");
            });
        }
    }

    private sealed class IdentityCommandProbe(CatalogItemKey key) : DbCommandInterceptor
    {
        public bool Exists;
        public int Reads;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            AssertPredicate(command, "RobloxAssetId", key.Id);
            AssertPredicate(command, "ItemType", key.Type.ToString());
            var table = new DataTable();
            table.Columns.Add("exists", typeof(bool));
            table.Rows.Add(Exists);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }

        private static void AssertPredicate(DbCommand command, string column, object expected)
        {
            var predicate = Regex.Match(command.CommandText, "\"" + column + "\" = @(?<parameter>[A-Za-z0-9_]+)");
            Check.That(predicate.Success, $"Identity predicate missing for {column}");
            var parameter = command.Parameters.Cast<DbParameter>()
                .Single(value => value.ParameterName.TrimStart('@') == predicate.Groups["parameter"].Value);
            Check.That(Equals(parameter.Value, expected), $"Query used the wrong identity value for {column}");
        }
    }
}
