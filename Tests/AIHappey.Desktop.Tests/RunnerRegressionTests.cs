internal static class RunnerRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var called = new List<string>();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Func<Task> Resolve(string name) => () =>
        {
            called.Add(name);
            return name == "fails" ? Task.FromException(new InvalidOperationException("fixture assertion")) : Task.CompletedTask;
        };
        var failures = await RegressionSuites.ExecuteAsync(["fails", "later"], Resolve, output, errors);
        check(failures == 1 && called.SequenceEqual(["fails", "later"]), "runner: failure is counted and later suites still execute");
        check(errors.ToString().Contains("SUITE FAIL: fails") && errors.ToString().Contains("fixture assertion")
            && output.ToString().Contains("SUITE PASS: later") && !output.ToString().Contains("SUITE PASS: fails"),
            "runner: original failure details are retained and never reported as a pass");
        called.Clear();
        failures = await RegressionSuites.ExecuteAsync(["selected"], Resolve, output, errors);
        check(failures == 0 && called.SequenceEqual(["selected"]), "runner: focused selection has no unrelated prerequisites");
    }
}
