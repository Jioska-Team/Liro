namespace Liro.RegressionTests;

internal sealed class RegressionSuite
{
    private readonly List<(string Name, Func<Task> Run)> tests = [];
    public void Test(string name, Action action) => AsyncTest(name, () =>
    {
        action();
        return Task.CompletedTask;
    });
    public void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
    public async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}: {exception}");
            }
        }

        Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
        return failures == 0 ? 0 : 1;
    }
}
