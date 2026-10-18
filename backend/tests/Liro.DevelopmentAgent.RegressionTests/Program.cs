using Liro.RegressionTests;
using Liro.DevelopmentAgent.RegressionTests;

var suite = new RegressionSuite();
ConnectionMessageTests.Register(suite);
SharedPanelTests.Register(suite);
ConfigurationTests.Register(suite);
SetupTests.Register(suite);
DashboardTests.Register(suite);
StartupTests.Register(suite);
BoundaryTests.Register(suite);
LifecycleBoundaryTests.Register(suite);
return await suite.RunAsync();


