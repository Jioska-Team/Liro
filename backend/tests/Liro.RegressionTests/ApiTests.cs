using System.Text.Json;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Liro.RegressionTests;

internal static class ApiTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("Writes require explicit administrative authorization", () =>
        {
            var controller = typeof(Liro.Api.Controllers.ItemsController);
            foreach (var method in new[]
            {
                "Create",
                "Import"
            }

            )
            {
                var attributes = controller.GetMethod(method)!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true);
                Check.That(attributes.Length > 0, $"{method} is publicly writable");
            }
        });
        suite.AsyncTest("API response does not expose domain event state", async () =>
        {
            var repo = new MemoryRepository(Fixtures.Item());
            var controller = new Liro.Api.Controllers.ItemsController(new ItemService(repo), Fixtures.Importer(repo));
            var result = (Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.GetByAssetId(1, CancellationToken.None);
            Check.That(!JsonSerializer.Serialize(result.Value).Contains("DomainEvents"), "API leaks internal domain state");
        });
        foreach (var authCase in new[]
        {
            ("valid", true, true, true),
            ("wrong", true, false, false),
            ("missing config", false, true, false),
            ("plain HTTP", true, true, false)
        }

        )
        {
            suite.AsyncTest("Admin authentication: " + authCase.Item1, async () =>
            {
                var key = new string('a', 40);
                var services = new ServiceCollection();
                services.AddLogging();
                var values = new Dictionary<string, string?>();
                if (authCase.Item2)
                {
                    values["Security:AdminApiKey"] = key;
                }

                var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
                Liro.Api.ApiServiceExtensions.AddLiroApi(services, config);
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    RequestServices = scope.ServiceProvider
                };
                context.Request.Scheme = authCase.Item1 == "plain HTTP" ? "http" : "https";
                context.Request.Headers["X-Liro-Admin-Key"] = authCase.Item3 ? key : new string('b', 40);
                var result = await context.AuthenticateAsync(Liro.Api.Security.AdminAuthenticationOptions.SchemeName);
                Check.That(result.Succeeded == authCase.Item4, "Administrative authentication decision is incorrect");
            });
        }

        foreach (var scheme in new[] { "http", "https" })
        {
            suite.AsyncTest("Anonymous request has no admin authentication failure: " + scheme, async () =>
            {
                var services = new ServiceCollection();
                services.AddLogging();
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:AdminApiKey"] = new string('a', 40)
                }).Build();
                Liro.Api.ApiServiceExtensions.AddLiroApi(services, config);
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
                context.Request.Scheme = scheme;
                context.Request.Path = "/api/lan/ready";
                var result = await context.AuthenticateAsync(Liro.Api.Security.AdminAuthenticationOptions.SchemeName);
                Check.That(result.None && result.Failure is null && !result.Succeeded,
                    "Anonymous readiness request incorrectly reports an administrative authentication failure");
                var authorization = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
                var decision = await authorization.AuthorizeAsync(context.User, context, "CatalogAdmin");
                Check.That(!decision.Succeeded, "Anonymous requests must not gain administrative access");
                await context.ChallengeAsync(Liro.Api.Security.AdminAuthenticationOptions.SchemeName);
                Check.That(context.Response.StatusCode == 401, "Missing credentials must still produce an administrative challenge");
            });
        }
        suite.Test("MVC validates the actual create request without server errors", () =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddControllers();
            using var provider = services.BuildServiceProvider();
            var action = new Microsoft.AspNetCore.Mvc.ActionContext(
                new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider },
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
            var validator = provider.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator>();
            validator.Validate(action, null, "", new Liro.Api.Controllers.CreateItemRequest(-1, "", null, (ItemMarketStatus)99));
            Check.That(!action.ModelState.IsValid && action.ModelState.ErrorCount >= 3, "Invalid create request was accepted");
        });
    }
}
