using Liro.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Explicit sandbox-only entry point. Does not run DatabaseInitializationService or workers.
try
{
    if (args.Length != 1 || args[0] != "--apply-sandbox" ||
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") != "Staging")
        throw new InvalidOperationException("Sandbox migration requires Staging and --apply-sandbox.");
    var connection = new NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("ConnectionStrings__lirodb") ?? "");
    if (connection.Host != "sandbox-postgres" || connection.Port != 5432 ||
        connection.Database != "liro_sandbox" || connection.Username != "liro_sandbox" ||
        string.IsNullOrWhiteSpace(connection.Password))
        throw new InvalidOperationException("Only the isolated Compose sandbox database is accepted.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    await using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>()
        .UseNpgsql(connection.ConnectionString).Options);
    await db.Database.MigrateAsync(deadline.Token);
    Console.WriteLine("Sandbox migrations completed.");
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Sandbox migration failed. Check sandbox identity, connectivity and migration state. Credentials are not logged.");
    return 1;
}
