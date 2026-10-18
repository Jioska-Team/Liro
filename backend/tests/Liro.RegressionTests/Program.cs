using Liro.RegressionTests;

var suite = new RegressionSuite();
MarketTrackingTests.Register(suite);
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



