using Liro.RegressionTests;
using Liro.DevelopmentAgent.RegressionTests;

var suite = new RegressionSuite();
ConfigurationTests.Register(suite);
SetupTests.Register(suite);
StartupTests.Register(suite);
BoundaryTests.Register(suite);
return await suite.RunAsync();

