using Liro.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Liro.Api;

// Documentation only. Authentication, rate limits and response formatting remain in their existing middleware.
internal static class ApiOpenApi
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[AdminAuthenticationOptions.SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Liro-Admin-Key",
                Description = "Administrative operations only, over HTTPS. Never embed this key in public clients."
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
            {
                operation.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(AdminAuthenticationOptions.SchemeName, context.Document)] = []
                }];
            }
            if (operation.Responses?.TryGetValue("400", out var badRequest) == true && badRequest is OpenApiResponse invalid)
            {
                invalid.Description = "Invalid input. Binding and validation errors include an errors dictionary; business validation may return only ProblemDetails.";
                var validation = await context.GetOrCreateSchemaAsync(typeof(ValidationProblemDetails), null, cancellationToken);
                if (invalid.Content is not null)
                    foreach (var media in invalid.Content.Values)
                        if (media.Schema is { } problem)
                            media.Schema = new OpenApiSchema { AnyOf = [problem, validation] };
            }
            if (operation.Responses?.TryGetValue("201", out var createdResponse) == true && createdResponse is OpenApiResponse created)
            {
                created.Headers ??= new Dictionary<string, IOpenApiHeader>();
                created.Headers["Location"] = new OpenApiHeader
                {
                    Description = "URL of the created item.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" }
                };
            }
            if (context.Description.RelativePath?.Contains("import", StringComparison.Ordinal) == true
                && operation.Responses?.TryGetValue("503", out var unavailableResponse) == true && unavailableResponse is OpenApiResponse unavailable)
            {
                unavailable.Headers ??= new Dictionary<string, IOpenApiHeader>();
                unavailable.Headers["Retry-After"] = new OpenApiHeader
                {
                    Description = "Delay in seconds, present when Roblox signals a cooldown; not present for all service failures.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                };
            }
            if (operation.Responses?.TryGetValue("429", out var response) == true && response is OpenApiResponse rateLimit)
            {
                rateLimit.Description = "Too many requests. Retry after the interval in Retry-After.";
                rateLimit.Headers ??= new Dictionary<string, IOpenApiHeader>();
                rateLimit.Headers["Retry-After"] = new OpenApiHeader
                {
                    Description = "Minimum delay in seconds before retrying.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" }
                };
                // The rate-limiter writes ProblemDetails using Results.Problem, not MVC formatters.
                if (rateLimit.Content?.Values.FirstOrDefault() is { } media)
                    rateLimit.Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = media };
            }
        });
    }
}
