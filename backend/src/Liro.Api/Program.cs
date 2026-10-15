using Liro.Api;
using Liro.Api.Development;
using Liro.Api.Security;
using Liro.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var lanAccess = DevelopmentLanAccess.Create(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddCors(lanAccess.ConfigureCors);
builder.Services.AddLiroApi(builder.Configuration);
builder.AddServiceDefaults();
builder.AddInfrastructure();
var app = builder.Build();
app.Use(lanAccess.InvokeAsync);
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWhen(context => !lanAccess.IsLanRequest(context), local => local.UseHttpsRedirection());
app.UseRouting();
if (lanAccess.Enabled)
{
    app.UseWhen(lanAccess.IsLanRequest, lan => lan.UseCors(DevelopmentLanAccess.CorsPolicyName));
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapLanReadiness(lanAccess);
app.MapDefaultEndpoints();
await app.RunAsync();
