using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

public sealed class LiroDbContext(DbContextOptions<LiroDbContext> options) : DbContext(options)
{

}