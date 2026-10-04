using Liro.Infrastructure;
using Liro.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.AddServiceDefaults();
builder.AddInfrastructure();

var host = builder.Build();
host.Run();
