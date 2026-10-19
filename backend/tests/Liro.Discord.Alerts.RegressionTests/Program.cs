using Liro.RegressionTests;
using Liro.Discord.Alerts.RegressionTests;
var suite = new RegressionSuite();
BandwidthTests.Register(suite);
BandwidthApiTests.Register(suite);
HostTests.Register(suite);
return await suite.RunAsync();
