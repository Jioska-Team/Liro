namespace Liro.Api.Contracts;

/// <summary>Development-only dependency readiness; latency is null when Valkey is unavailable.</summary>
public sealed record InfrastructureResponse(string Postgres, string Valkey, double? ValkeyLatencyMs);
