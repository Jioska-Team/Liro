using Liro.Api;
using Liro.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLiroApi(builder.Configuration);
builder.AddServiceDefaults();
builder.AddInfrastructure();
var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapDefaultEndpoints();
await app.RunAsync();
