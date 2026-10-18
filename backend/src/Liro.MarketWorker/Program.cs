using Liro.Infrastructure.Persistence;
using Liro.MarketWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<LiroDbContext>("lirodb");
builder.AddRedisClient("valkey");
builder.Services.AddOptions<MarketOptions>().BindConfiguration("Market").Validate(x => x.IsValid(), "Configuración de mercado inválida.").ValidateOnStart();
builder.Services.AddSingleton<MarketTransport>();
builder.Services.AddHostedService<MarketWorkerService>();
await builder.Build().RunAsync();
