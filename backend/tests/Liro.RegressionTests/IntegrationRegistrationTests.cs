namespace Liro.RegressionTests;

internal static class IntegrationRegistrationTests
{
    public static void Register(RegressionSuite suite)
    {
        (string Name, string Variable, Action<RegressionSuite> Register)[] fixtures =
        [
            ("GlobalBudget", "LIRO_TEST_REDIS", GlobalBudgetTests.Register),
            ("KnownWatchOrdering", "LIRO_TEST_POSTGRES", KnownWatchOrderingTests.Register),
            ("MarketConcurrency", "LIRO_TEST_POSTGRES", MarketConcurrencyTests.Register),
            ("MarketDirectFallback", "LIRO_TEST_REDIS", MarketDirectFallbackTests.Register),
            ("MarketPriority", "LIRO_TEST_POSTGRES", MarketPriorityTests.Register),
            ("MarketTracking", "LIRO_TEST_POSTGRES", MarketTrackingTests.Register),
            ("PersistenceOptimization", "LIRO_TEST_POSTGRES", PersistenceOptimizationTests.Register),
            ("ReservationRecovery", "LIRO_TEST_REDIS", ReservationRecoveryTests.Register),
            ("SharedCooldown", "LIRO_TEST_REDIS", SharedCooldownTests.Register),
            ("StartupHistory", "LIRO_TEST_POSTGRES", StartupHistoryTests.Register)
        ];
        foreach (var fixture in fixtures)
        {
            suite.Test($"IntegrationRegistration_{fixture.Name}_RejectsMissingConnection", () =>
            {
                var original = Environment.GetEnvironmentVariable(fixture.Variable);
                try
                {
                    Environment.SetEnvironmentVariable(fixture.Variable, null);
                    InvalidOperationException? failure = null;
                    try
                    {
                        fixture.Register(new RegressionSuite());
                    }
                    catch (InvalidOperationException error) { failure = error; }
                    Check.That(failure is not null && failure.Message.Contains(fixture.Variable),
                        "Fixture silently omitted integration cases instead of rejecting the missing connection.");
                }
                finally { Environment.SetEnvironmentVariable(fixture.Variable, original); }
            });
        }
    }
}
