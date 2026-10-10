using Liro.RegressionTests;

var suite = new RegressionSuite();
CatalogTests.Register(suite);
MarketTests.Register(suite);
MarketHistoryTests.Register(suite);
RobloxTests.Register(suite);
PersistenceTests.Register(suite);
WorkerTests.Register(suite);
ApiTests.Register(suite);
return await suite.RunAsync();
