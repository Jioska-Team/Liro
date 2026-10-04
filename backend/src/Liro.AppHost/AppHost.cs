var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume();

var database = postgres
    .AddDatabase("lirodb");

var valkey = builder
    .AddValkey("valkey")
    .WithDataVolume();

builder
    .AddProject<Projects.Liro_Api>("api")
    .WithReference(database)
    .WithReference(valkey)
    .WaitFor(database)
    .WaitFor(valkey);

builder
    .AddProject<Projects.Liro_Workers>("workers")
    .WithReference(database)
    .WithReference(valkey)
    .WaitFor(database)
    .WaitFor(valkey);

builder.Build().Run();