using Liro.RegressionTests;
using Liro.Discord.LocalControl.RegressionTests;

var suite = new RegressionSuite();
GatewayDiagnosticsTests.Register(suite);
if (args.Contains("--gateway-only")) return await suite.RunAsync();
CommandTimeoutTests.Register(suite);
if (args.Contains("--command-timeouts-only")) return await suite.RunAsync();
ConnectionMessageTests.Register(suite);
SharedPanelTests.Register(suite);
ConfigurationTests.Register(suite);
SetupTests.Register(suite);
DashboardTests.Register(suite);
StartupTests.Register(suite);
BoundaryTests.Register(suite);
LifecycleBoundaryTests.Register(suite);
return await suite.RunAsync();
