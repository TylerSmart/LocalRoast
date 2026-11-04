using System.Diagnostics;

namespace App.Client.E2ETests;

[TestClass]
public static class AssemblyInitializer
{
    private static PlaywrightServerFixture? _server;

    public static string ServerUrl =>
        _server?.ServerUrl
        ?? throw new InvalidOperationException("E2E Test server is not initialized.");

    public static int GetServerLogCursor() => _server?.GetLogCursor() ?? 0;

    public static IReadOnlyList<string> GetServerLogsSince(int cursor) =>
        _server?.GetLogsSince(cursor) ?? [];

    [AssemblyInitialize]
    public static async Task AssemblyInit(TestContext context)
    {
        // Automatically launch a visible browser window when debugging E2E tests from Visual Studio / IDE
        if (
            Debugger.IsAttached
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEADED"))
        )
        {
            Environment.SetEnvironmentVariable("HEADED", "1");
        }

        _server = await PlaywrightServerFixture.StartAsync();
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
