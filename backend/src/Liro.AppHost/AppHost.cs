using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var database = postgres.AddDatabase("lirodb");
var valkey = builder.AddValkey("valkey").WithDataVolume();
var api = builder.AddProject<Projects.Liro_Api>("api").WithReference(database).WithReference(valkey).WaitFor(database).WaitFor(valkey);
if (builder.Configuration.GetValue<bool>("Lan:Enabled"))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("LAN sharing is only available in Development.");
    }

    var address = builder.Configuration["Lan:Address"];
    if (string.IsNullOrWhiteSpace(address))
    {
        throw new InvalidOperationException("LAN sharing requires a local IPv4 address.");
    }

    api.WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5205;
        endpoint.TargetPort = 5205;
        endpoint.TargetHost = address;
        endpoint.IsProxied = false;
    });
    api.WithEnvironment("Lan__Enabled", "true")
        .WithEnvironment("Lan__Address", address)
        .WithEnvironment("Lan__PrefixLength", builder.Configuration["Lan:PrefixLength"] ?? "");
    foreach (var origin in builder.Configuration.GetSection("Lan:AllowedOrigins").GetChildren())
    {
        api.WithEnvironment($"Lan__AllowedOrigins__{origin.Key}", origin.Value ?? "");
    }
}
builder.AddProject<Projects.Liro_Workers>("workers").WithReference(database).WithReference(valkey).WaitFor(database).WaitFor(valkey);
var app = builder.Build();
app.Run();
