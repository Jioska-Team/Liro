using Npgsql;
using StackExchange.Redis;

namespace Liro.RegressionTests;

internal static class IntegrationPrerequisites
{
    public static string RequireConnection(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required integration connection {variable} is missing. Integration tests cannot be skipped.");
        return value;
    }

    public static async Task<bool> CheckAsync()
    {
        var postgres = Environment.GetEnvironmentVariable("LIRO_TEST_POSTGRES");
        var valkey = Environment.GetEnvironmentVariable("LIRO_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(postgres) || string.IsNullOrWhiteSpace(valkey))
        {
            Console.Error.WriteLine("FAIL Integration_RequiredConnections: configure LIRO_TEST_POSTGRES and LIRO_TEST_REDIS. No tests executed.");
            return false;
        }

        var dependency = "PostgreSQL";
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(postgres) { Timeout = 5, CommandTimeout = 5 };
            // Integration fixtures may create schemas. Never target the application database.
            if (!settings.Database!.StartsWith("liro_test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Use a separate liro_test_ database.");
            await using (var connection = new NpgsqlConnection(settings.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                if (!Equals(await command.ExecuteScalarAsync(), 1))
                    throw new InvalidOperationException();
            }
            Console.WriteLine("PASS Integration_PostgreSqlAvailable");
            dependency = "Valkey";
            var options = ConfigurationOptions.Parse(valkey);
            if (options.DefaultDatabase is null or <= 0)
                throw new InvalidOperationException("Select a nonzero logical database for integration tests.");
            options.AbortOnConnectFail = true;
            options.ConnectRetry = 0;
            options.ConnectTimeout = 5000;
            options.AsyncTimeout = 5000;
            using var redis = await ConnectionMultiplexer.ConnectAsync(options);
            await redis.GetDatabase().PingAsync();
            Console.WriteLine("PASS Integration_ValkeyAvailable");
            return true;
        }
        catch (Exception error)
        {
            // Connection strings and server exception messages can contain credentials.
            Console.Error.WriteLine($"FAIL Integration_{dependency}Available ({error.GetType().Name}). Use an isolated liro_test_ database and a nonzero Valkey database. No tests executed.");
            return false;
        }
    }
}
