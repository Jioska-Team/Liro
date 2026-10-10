using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Liro.Infrastructure.Persistence;

public sealed class LiroDbContextFactory : IDesignTimeDbContextFactory<LiroDbContext>
{
    public LiroDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__lirodb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set ConnectionStrings__lirodb to the intended database connection string. No default credentials or port are assumed.");
        }

        return new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql(connectionString).Options);
    }
}
