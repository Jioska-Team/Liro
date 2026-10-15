using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Liro.Api.Security;

public sealed class DevelopmentLanAccess
{
    public const string CorsPolicyName = "DevelopmentLanReadOnly";
    public const int Port = 5205;
    private readonly uint network;
    private readonly uint mask;
    private readonly string[] origins;

    private DevelopmentLanAccess(bool enabled, uint network = 0, uint mask = 0, string[]? origins = null)
    {
        Enabled = enabled;
        this.network = network;
        this.mask = mask;
        this.origins = origins ?? [];
    }

    public bool Enabled
    {
        get;
    }

    public static DevelopmentLanAccess Create(IConfiguration configuration, string environmentName)
    {
        if (!configuration.GetValue<bool>("Lan:Enabled"))
        {
            return new DevelopmentLanAccess(false);
        }

        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LAN sharing is only available in Development.");
        }

        var prefix = configuration.GetValue<int>("Lan:PrefixLength");
        if (!IPAddress.TryParse(configuration["Lan:Address"], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork || prefix is < 8 or > 30)
        {
            throw new InvalidOperationException("LAN sharing requires a private IPv4 address and a subnet prefix between 8 and 30.");
        }

        var addressValue = ToNumber(address);
        var subnetMask = uint.MaxValue << (32 - prefix);
        var subnet = addressValue & subnetMask;
        if (!IsPrivate(subnet) || !IsPrivate(subnet | ~subnetMask)
            || addressValue == subnet || addressValue == (subnet | ~subnetMask))
        {
            throw new InvalidOperationException("The entire LAN subnet must belong to a private IPv4 range.");
        }

        var allowedOrigins = configuration.GetSection("Lan:AllowedOrigins").Get<string[]>() ?? [];
        if (allowedOrigins.Length == 0 || allowedOrigins.Any(origin =>
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https")
            || uri.GetLeftPart(UriPartial.Authority) != origin
            || !string.IsNullOrEmpty(uri.UserInfo) || origin.Contains('*')))
        {
            throw new InvalidOperationException("LAN sharing requires explicit frontend origins without paths or wildcards.");
        }

        var access = new DevelopmentLanAccess(true, subnet, subnetMask, allowedOrigins);
        return access;
    }

    public bool IsLanRequest(HttpContext context)
    {
        var isLanRequest = Enabled && context.Connection.LocalPort == Port;
        return isLanRequest;
    }

    public void ConfigureCors(CorsOptions options)
    {
        if (Enabled)
        {
            options.AddPolicy(CorsPolicyName, policy => policy.WithOrigins(origins)
                .WithMethods("GET", "HEAD").WithHeaders("Accept", "Content-Type"));
        }
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (IsLanRequest(context))
        {
            var peer = context.Connection.RemoteIpAddress;
            if (peer?.IsIPv4MappedToIPv6 == true)
            {
                peer = peer.MapToIPv4();
            }

            var allowedPeer = peer?.AddressFamily == AddressFamily.InterNetwork
                && (ToNumber(peer) & mask) == network;
            var allowedMethod = HttpMethods.IsGet(context.Request.Method)
                || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
            var allowedPath = context.Request.Path.StartsWithSegments("/api/v1/items")
                || context.Request.Path.Equals(new PathString("/api/lan/ready"));
            if (!allowedPeer || !allowedMethod || !allowedPath)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next(context);
    }

    private static uint ToNumber(IPAddress address)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        return value;
    }

    private static bool IsPrivate(uint address)
    {
        var isPrivate = (address & 0xff000000) == 0x0a000000
            || (address & 0xfff00000) == 0xac100000
            || (address & 0xffff0000) == 0xc0a80000;
        return isPrivate;
    }
}
