using Liro.RegressionTests;

// Never report a green run after silently omitting database-dependent cases.
if (!await IntegrationPrerequisites.CheckAsync()) return 2;
if (args.Contains("--check-integration")) return 0;

var suite = new RegressionSuite();
ValkeyLossTests.Register(suite);
if (args.Contains("--valkey-loss-only")) return await suite.RunAsync();
IntegrationRegistrationTests.Register(suite);
CatalogReadTests.Register(suite);
if (args.Contains("--catalog-reads-only")) return await suite.RunAsync();
ImportRouteTests.Register(suite);
if (args.Contains("--import-routes-only")) return await suite.RunAsync();
MarketSeriesTests.Register(suite);
if (args.Contains("--market-series-only")) return await suite.RunAsync();
MarketPriorityTests.Register(suite);
if (args.Contains("--market-priority-only")) return await suite.RunAsync();
SharedCooldownTests.Register(suite);
if (args.Contains("--shared-cooldown-only")) return await suite.RunAsync();
if (args.Contains("--startup-history-only"))
{
    StartupHistoryTests.Register(suite);
    return await suite.RunAsync();
}
ReservationRecoveryTests.Register(suite);
StartupHistoryTests.Register(suite);
if (args.Contains("--recovery-only")) return await suite.RunAsync();
MarketDirectFallbackTests.Register(suite);
if (args.Contains("--market-direct-only")) return await suite.RunAsync();
SandboxHealthTests.Register(suite);
if (args.Contains("--sandbox-health-only")) return await suite.RunAsync();
CsrfDiagnosticsTests.Register(suite);
if (args.Contains("--csrf-diagnostics-only")) return await suite.RunAsync();
MarketLifecycleTests.Register(suite);
if (args.Contains("--market-lifecycle-only")) return await suite.RunAsync();
MarketProxyFailureTests.Register(suite);
MarketTrackingTests.Register(suite);
MarketConcurrencyTests.Register(suite);
PersistenceOptimizationTests.Register(suite);
if (args.Contains("--market-tracking-only")) return await suite.RunAsync();
KnownWatchOrderingTests.Register(suite);
GlobalBudgetTests.Register(suite);
MainScanPriorityTests.Register(suite);
RequestOptimizationTests.Register(suite);
ComponentRecoveryTests.Register(suite);
BundleRelationTests.Register(suite);
LimitedCoverageTests.Register(suite);
CatalogTests.Register(suite);
PriorityRefreshTests.Register(suite);
SearchDetailsTests.Register(suite);
DatabaseBatchTests.Register(suite);
BundleTests.Register(suite);
BatchTests.Register(suite);
CoverageTests.Register(suite);
MarketTests.Register(suite);
MarketHistoryTests.Register(suite);
RobloxTests.Register(suite);
PersistenceTests.Register(suite);
WorkerTests.Register(suite);
ApiTests.Register(suite);
LanAccessTests.Register(suite);
LanReadinessTests.Register(suite);
return await suite.RunAsync();
