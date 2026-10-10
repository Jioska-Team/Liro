using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Liro.Api.Security;

public sealed class AdminAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "AdminKey";
    public string? ApiKey
    {
        get; set;
    }
}

public sealed class AdminAuthenticationHandler(IOptionsMonitor<AdminAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AdminAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.IsHttps)
        {
            return Task.FromResult(AuthenticateResult.Fail("Administrative requests require HTTPS."));
        }

        var supplied = Request.Headers["X-Liro-Admin-Key"];
        if (string.IsNullOrWhiteSpace(Options.ApiKey) || Options.ApiKey.Length < 32 || supplied.Count != 1 || supplied[0] is not { Length: >= 32 and <= 1024 } key)
        {
            return Task.FromResult(AuthenticateResult.Fail("Administrative credentials required."));
        }

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(Options.ApiKey));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid administrative credentials."));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "catalog-admin")], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
