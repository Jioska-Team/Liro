using Liro.Application.Catalog.Services;
using Liro.Infrastructure.Persistence.Repositories;
using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
// El trabajador reprograma los fallos; el guardado usa una transacción explícita.
builder.AddNpgsqlDbContext<LiroDbContext>("lirodb", settings => settings.DisableRetry = true);
builder.AddRedisClient("valkey");
builder.Services.AddOptions<MarketOptions>().BindConfiguration("Market").Validate(x => x.IsValid(), "Configuración de mercado inválida.").ValidateOnStart();
builder.Services.AddOptions<MarketDashboardOptions>().BindConfiguration("MarketDashboard").Validate(x => x.IsValid(), "Invalid dashboard settings.").ValidateOnStart();
builder.Services.AddScoped<IMarketDashboardRepository, MarketDashboardRepository>();
builder.Services.AddSingleton<MarketTransport>();
builder.Services.AddHostedService<MarketWorkerService>();
using var host = builder.Build();
MarketWorkerInstance instance;
try
{
    var lockPath = builder.Configuration["Market:InstanceLockPath"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Liro", "MarketWorker", "worker.lock");
    instance = MarketWorkerInstance.Acquire(lockPath);
}
catch (IOException)
{
    host.Services.GetRequiredService<ILogger<MarketWorkerService>>()
        .LogError("No se pudo adquirir el bloqueo exclusivo de mercado. No se iniciará otro trabajador.");
    Environment.ExitCode = 2;
    return;
}
using (instance)
    await host.RunAsync();
