using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace App.Client.E2ETests;

public abstract class E2ETestBase : PageTest
{
    /// <summary>
    /// Maximum execution timeout (in milliseconds) applied to every E2E test method.
    /// Prevents any test from hanging indefinitely.
    /// </summary>
    public const int DefaultTestTimeoutMs = 90_000;

    /// <summary>
    /// Default Playwright action timeout (clicks, fills, locators) in milliseconds.
    /// </summary>
    public const float DefaultActionTimeoutMs = 15_000;

    /// <summary>
    /// Default Playwright page navigation timeout in milliseconds.
    /// </summary>
    public const float DefaultNavigationTimeoutMs = 30_000;

    /// <summary>
    /// Default Playwright Expect(...) assertion timeout in milliseconds.
    /// </summary>
    public const float DefaultExpectTimeoutMs = 10_000;

    private static readonly TimeSpan ArtifactStepTimeout = TimeSpan.FromSeconds(10);
    private static readonly string RunId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly List<TrackedPageSession> _trackedSessions = [];
    private string? _tempVideoDir;
    private DateTime _testStartedAtUtc;
    private int _serverLogCursor;

    protected string ServerUrl => AssemblyInitializer.ServerUrl;

    /// <summary>
    /// Root git-ignored folder where debug artifacts for failed E2E test runs are saved.
    /// </summary>
    public static string DebugArtifactsRootDirectory =>
        Path.Combine(
            PlaywrightServerFixture.FindSolutionRoot(),
            "Tests",
            "Client.E2ETests",
            "DebugArtifacts"
        );

    public override BrowserNewContextOptions ContextOptions()
    {
        _tempVideoDir ??= Path.Combine(
            Path.GetTempPath(),
            $"LocalRoast_E2E_Video_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempVideoDir);

        return new BrowserNewContextOptions
        {
            RecordVideoDir = _tempVideoDir,
            RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 },
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
        };
    }

    /// <summary>
    /// True when a debugger is attached (e.g. "Debug Test" in Visual Studio) or PWDEBUG=1 is set.
    /// Disables Playwright action/navigation timeouts so stepping through breakpoints never times out.
    /// </summary>
    protected static bool IsInteractiveDebugSession =>
        Debugger.IsAttached || Environment.GetEnvironmentVariable("PWDEBUG") == "1";

    [TestInitialize]
    public async Task E2ETestInitializeAsync()
    {
        _testStartedAtUtc = DateTime.UtcNow;
        _serverLogCursor = AssemblyInitializer.GetServerLogCursor();

        SetDefaultExpectTimeout(DefaultExpectTimeoutMs);
        Context.SetDefaultTimeout(IsInteractiveDebugSession ? 0 : DefaultActionTimeoutMs);
        Context.SetDefaultNavigationTimeout(
            IsInteractiveDebugSession ? 0 : DefaultNavigationTimeoutMs
        );

        await RegisterPageSessionAsync(Context, Page, "primary");
    }

    [TestCleanup]
    public async Task E2ETestCleanupAsync()
    {
        var isFailed = TestContext.CurrentTestOutcome != UnitTestOutcome.Passed;

        try
        {
            if (isFailed)
            {
                await SaveDebugArtifactsAsync(
                    $"Outcome: {TestContext.CurrentTestOutcome}",
                    closeContexts: true
                );
            }
            else
            {
                foreach (var session in _trackedSessions)
                {
                    if (session.TracingActive)
                    {
                        try
                        {
                            await session
                                .Context.Tracing.StopAsync()
                                .WaitAsync(ArtifactStepTimeout);
                            session.TracingActive = false;
                        }
                        catch
                        {
                            // Best-effort cleanup
                        }
                    }

                    try
                    {
                        await session.Context.CloseAsync().WaitAsync(ArtifactStepTimeout);
                    }
                    catch
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }
        finally
        {
            _trackedSessions.Clear();

            if (!string.IsNullOrEmpty(_tempVideoDir) && Directory.Exists(_tempVideoDir))
            {
                try
                {
                    Directory.Delete(_tempVideoDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    /// Creates an additional isolated browser context and page that is automatically configured
    /// with timeouts, tracing, console/network logging, and video/screenshot capture on test failure.
    /// </summary>
    protected async Task<(IBrowserContext Context, IPage Page)> CreateIsolatedPageAsync(
        string label = "secondary"
    )
    {
        _tempVideoDir ??= Path.Combine(
            Path.GetTempPath(),
            $"LocalRoast_E2E_Video_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempVideoDir);

        var context = await Browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                RecordVideoDir = _tempVideoDir,
                RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 },
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            }
        );

        context.SetDefaultTimeout(IsInteractiveDebugSession ? 0 : DefaultActionTimeoutMs);
        context.SetDefaultNavigationTimeout(
            IsInteractiveDebugSession ? 0 : DefaultNavigationTimeoutMs
        );

        var page = await context.NewPageAsync();
        await RegisterPageSessionAsync(context, page, label);

        return (context, page);
    }

    /// <summary>
    /// Saves all debug artifacts (screenshots, videos, DOM HTML, page state JSON, console logs,
    /// network error logs, Playwright trace, and API server logs) for the current test run.
    /// </summary>
    protected async Task<string> SaveDebugArtifactsAsync(
        string? reason = null,
        bool closeContexts = true
    )
    {
        var className = SanitizeFileName(
            TestContext.FullyQualifiedTestClassName?.Split('.').LastOrDefault() ?? GetType().Name
        );
        var testName = SanitizeFileName(TestContext.TestName ?? "UnknownTest");
        var artifactDir = Path.Combine(
            DebugArtifactsRootDirectory,
            RunId,
            $"{className}_{testName}_{DateTime.UtcNow:HHmmss_fff}"
        );

        Directory.CreateDirectory(artifactDir);

        for (var i = 0; i < _trackedSessions.Count; i++)
        {
            var session = _trackedSessions[i];
            var prefix =
                _trackedSessions.Count == 1
                    ? string.Empty
                    : $"{i + 1}_{SanitizeFileName(session.Label)}_";

            await CaptureSessionArtifactsAsync(session, artifactDir, prefix, reason, closeContexts);
        }

        // Save API server stdout/stderr logs emitted during the test
        try
        {
            var serverLogs = AssemblyInitializer.GetServerLogsSince(_serverLogCursor);
            var serverLogPath = Path.Combine(artifactDir, "api-server.log");
            await File.WriteAllLinesAsync(
                serverLogPath,
                serverLogs.Count > 0 ? serverLogs : ["(No API server logs emitted during test)"]
            );
            TryAddResultFile(serverLogPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[E2E DebugArtifacts] Failed to write API logs: {ex.Message}");
        }

        TestContext.WriteLine($"[E2E DebugArtifacts] Saved failure artifacts to: {artifactDir}");
        Console.Error.WriteLine($"[E2E DebugArtifacts] Saved failure artifacts to: {artifactDir}");

        return artifactDir;
    }

    private async Task RegisterPageSessionAsync(IBrowserContext context, IPage page, string label)
    {
        var session = new TrackedPageSession(context, page, label);

        page.Console += (_, msg) =>
        {
            var location = string.IsNullOrEmpty(msg.Location) ? string.Empty : $" ({msg.Location})";
            session.AddConsoleLog(
                $"[{DateTime.UtcNow:O}] [{msg.Type.ToUpperInvariant()}] {msg.Text}{location}"
            );
        };

        page.PageError += (_, error) =>
        {
            session.AddConsoleLog($"[{DateTime.UtcNow:O}] [PAGE_ERROR] {error}");
        };

        page.RequestFailed += (_, request) =>
        {
            session.AddNetworkLog(
                $"[{DateTime.UtcNow:O}] [REQUEST_FAILED] {request.Method} {request.Url} -> {request.Failure}"
            );
        };

        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                session.AddNetworkLog(
                    $"[{DateTime.UtcNow:O}] [HTTP_{response.Status}] {response.Request.Method} {response.Url}"
                );
            }
        };

        try
        {
            await context
                .Tracing.StartAsync(
                    new TracingStartOptions
                    {
                        Screenshots = true,
                        Snapshots = true,
                        Sources = true,
                    }
                )
                .WaitAsync(ArtifactStepTimeout);
            session.TracingActive = true;
        }
        catch
        {
            // Continue even if tracing fails to start
        }

        _trackedSessions.Add(session);
    }

    private async Task CaptureSessionArtifactsAsync(
        TrackedPageSession session,
        string artifactDir,
        string prefix,
        string? reason,
        bool closeContext
    )
    {
        var page = session.Page;
        var context = session.Context;

        // 1. Capture Full-Page Screenshot (fallback to viewport screenshot if full-page fails)
        var screenshotPath = Path.Combine(artifactDir, $"{prefix}screenshot.png");
        try
        {
            if (!page.IsClosed)
            {
                try
                {
                    await page.ScreenshotAsync(
                            new PageScreenshotOptions { Path = screenshotPath, FullPage = true }
                        )
                        .WaitAsync(ArtifactStepTimeout);
                }
                catch
                {
                    await page.ScreenshotAsync(
                            new PageScreenshotOptions { Path = screenshotPath, FullPage = false }
                        )
                        .WaitAsync(ArtifactStepTimeout);
                }
                TryAddResultFile(screenshotPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to capture screenshot ({session.Label}): {ex.Message}"
            );
        }

        // 2. Capture Rendered DOM HTML
        var htmlPath = Path.Combine(artifactDir, $"{prefix}page.html");
        try
        {
            if (!page.IsClosed)
            {
                var htmlContent = await page.ContentAsync().WaitAsync(ArtifactStepTimeout);
                await File.WriteAllTextAsync(htmlPath, htmlContent);
                TryAddResultFile(htmlPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to capture HTML ({session.Label}): {ex.Message}"
            );
        }

        // 3. Capture Structured Page Metadata & Storage (URL, title, viewport, localStorage, sessionStorage, dialogs)
        var pageDataPath = Path.Combine(artifactDir, $"{prefix}page-data.json");
        try
        {
            string? title = null;
            Dictionary<string, string?>? localStorage = null;
            Dictionary<string, string?>? sessionStorage = null;
            string[]? openDialogs = null;

            if (!page.IsClosed)
            {
                try
                {
                    title = await page.TitleAsync().WaitAsync(ArtifactStepTimeout);
                }
                catch
                {
                    // Ignore
                }

                try
                {
                    localStorage = await page.EvaluateAsync<Dictionary<string, string?>>(
                            @"() => {
                                const entries = {};
                                for (let i = 0; i < window.localStorage.length; i++) {
                                    const key = window.localStorage.key(i);
                                    if (key !== null) entries[key] = window.localStorage.getItem(key);
                                }
                                return entries;
                            }"
                        )
                        .WaitAsync(ArtifactStepTimeout);
                }
                catch
                {
                    // Page may be on about:blank
                }

                try
                {
                    sessionStorage = await page.EvaluateAsync<Dictionary<string, string?>>(
                            @"() => {
                                const entries = {};
                                for (let i = 0; i < window.sessionStorage.length; i++) {
                                    const key = window.sessionStorage.key(i);
                                    if (key !== null) entries[key] = window.sessionStorage.getItem(key);
                                }
                                return entries;
                            }"
                        )
                        .WaitAsync(ArtifactStepTimeout);
                }
                catch
                {
                    // Page may be on about:blank
                }

                try
                {
                    openDialogs = await page.EvaluateAsync<string[]>(
                            @"() => Array.from(document.querySelectorAll('mat-dialog-container, mat-mdc-snack-bar-container'))
                                .map(el => (el.textContent || '').trim())"
                        )
                        .WaitAsync(ArtifactStepTimeout);
                }
                catch
                {
                    // Ignore
                }
            }

            var metadata = new
            {
                TestClass = TestContext.FullyQualifiedTestClassName,
                TestName = TestContext.TestName,
                Outcome = TestContext.CurrentTestOutcome.ToString(),
                Reason = reason,
                SessionLabel = session.Label,
                StartedAtUtc = _testStartedAtUtc,
                CapturedAtUtc = DateTime.UtcNow,
                DurationMs = (DateTime.UtcNow - _testStartedAtUtc).TotalMilliseconds,
                Url = page.Url,
                Title = title,
                Viewport = page.ViewportSize,
                OpenDialogsAndSnackbars = openDialogs ?? [],
                LocalStorage = localStorage ?? [],
                SessionStorage = sessionStorage ?? [],
            };

            await File.WriteAllTextAsync(
                pageDataPath,
                JsonSerializer.Serialize(metadata, PrettyJsonOptions)
            );
            TryAddResultFile(pageDataPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to capture page-data.json ({session.Label}): {ex.Message}"
            );
        }

        // 4. Write Console & Uncaught Page Error Logs
        var consoleLogPath = Path.Combine(artifactDir, $"{prefix}console.log");
        try
        {
            var consoleLogs = session.GetConsoleLogs();
            await File.WriteAllLinesAsync(
                consoleLogPath,
                consoleLogs.Count > 0 ? consoleLogs : ["(No browser console messages recorded)"]
            );
            TryAddResultFile(consoleLogPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to write console.log ({session.Label}): {ex.Message}"
            );
        }

        // 5. Write Network Failures & HTTP >= 400 Error Logs
        var networkLogPath = Path.Combine(artifactDir, $"{prefix}network-errors.log");
        try
        {
            var networkLogs = session.GetNetworkLogs();
            await File.WriteAllLinesAsync(
                networkLogPath,
                networkLogs.Count > 0
                    ? networkLogs
                    : ["(No failed requests or HTTP >= 400 responses)"]
            );
            TryAddResultFile(networkLogPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to write network-errors.log ({session.Label}): {ex.Message}"
            );
        }

        // 6. Stop Playwright Tracing and save trace.zip
        if (session.TracingActive)
        {
            var tracePath = Path.Combine(artifactDir, $"{prefix}trace.zip");
            try
            {
                await context
                    .Tracing.StopAsync(new TracingStopOptions { Path = tracePath })
                    .WaitAsync(ArtifactStepTimeout);
                session.TracingActive = false;
                TryAddResultFile(tracePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[E2E DebugArtifacts] Failed to save trace.zip ({session.Label}): {ex.Message}"
                );
            }
        }

        // 7. Close context to flush video recording and save video.webm
        try
        {
            var video = page.Video;
            if (closeContext)
            {
                await context.CloseAsync().WaitAsync(ArtifactStepTimeout);
            }
            else if (!page.IsClosed)
            {
                await page.CloseAsync().WaitAsync(ArtifactStepTimeout);
            }

            if (video is not null)
            {
                var videoPath = Path.Combine(artifactDir, $"{prefix}video.webm");
                await video.SaveAsAsync(videoPath).WaitAsync(ArtifactStepTimeout);
                TryAddResultFile(videoPath);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[E2E DebugArtifacts] Failed to save video.webm ({session.Label}): {ex.Message}"
            );
        }
    }

    private void TryAddResultFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                TestContext.AddResultFile(path);
            }
        }
        catch
        {
            // Ignore if TestContext does not support result attachments in the current runner
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    protected async Task<(string Username, string Email)> RegisterAndLoginAsync(
        string? username = null,
        string? email = null,
        string password = "KitchenSecret123!"
    )
    {
        username ??= $"chef_{Guid.NewGuid():N}"[..12];
        email ??= $"chef_{Guid.NewGuid():N}@example.com";

        await Page.GotoAsync($"{ServerUrl}/login");
        await Page.Locator(".mat-mdc-tab", new() { HasText = "Create Account" }).ClickAsync();

        var registerForm = Page.Locator("form[aria-label='Create account form']");
        await registerForm.Locator("input[formcontrolname='username']").FillAsync(username);
        await registerForm.Locator("input[formcontrolname='email']").FillAsync(email);
        await registerForm.Locator("input[formcontrolname='password']").FillAsync(password);
        await registerForm.Locator("input[formcontrolname='confirmPassword']").FillAsync(password);

        await registerForm.Locator("button[type='submit']").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes.*"), new() { Timeout = 15000 });

        return (username, email);
    }

    private sealed class TrackedPageSession(IBrowserContext context, IPage page, string label)
    {
        private readonly Lock _lock = new();
        private readonly List<string> _consoleLogs = [];
        private readonly List<string> _networkLogs = [];

        public IBrowserContext Context { get; } = context;

        public IPage Page { get; } = page;

        public string Label { get; } = label;

        public bool TracingActive { get; set; }

        public void AddConsoleLog(string line)
        {
            lock (_lock)
            {
                _consoleLogs.Add(line);
            }
        }

        public void AddNetworkLog(string line)
        {
            lock (_lock)
            {
                _networkLogs.Add(line);
            }
        }

        public IReadOnlyList<string> GetConsoleLogs()
        {
            lock (_lock)
            {
                return _consoleLogs.ToList();
            }
        }

        public IReadOnlyList<string> GetNetworkLogs()
        {
            lock (_lock)
            {
                return _networkLogs.ToList();
            }
        }
    }
}
