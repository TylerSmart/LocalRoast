using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace App.Client.E2ETests;

public sealed class PlaywrightServerFixture : IAsyncDisposable
{
    private Process? _process;
    private string? _tempUploadsPath;

    public string ServerUrl { get; private set; } = string.Empty;

    public static async Task<PlaywrightServerFixture> StartAsync()
    {
        var fixture = new PlaywrightServerFixture();
        await fixture.InitializeAsync();
        return fixture;
    }

    private async Task InitializeAsync()
    {
        var port = GetFreePort();
        ServerUrl = $"http://127.0.0.1:{port}";

        var solutionDir = FindSolutionRoot();
        var apiDllPath = Path.Combine(solutionDir, "API", "bin", "Debug", "net10.0", "App.API.dll");
        var webRootPath = Path.Combine(solutionDir, "Client", "dist", "App.Client", "browser");

        if (!File.Exists(apiDllPath))
        {
            throw new FileNotFoundException(
                $"App.API.dll not found at '{apiDllPath}'. Build solution before running E2E tests."
            );
        }

        if (!File.Exists(Path.Combine(webRootPath, "index.html")))
        {
            throw new FileNotFoundException(
                $"index.html not found at '{webRootPath}'. Run 'npm run build:production' in Client/ first."
            );
        }

        var inMemoryDbName = $"E2E_Test_{Guid.NewGuid():N}";
        _tempUploadsPath = Path.Combine(
            Path.GetTempPath(),
            $"LocalRoast_E2E_Uploads_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_tempUploadsPath);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"exec \"{apiDllPath}\" --urls \"{ServerUrl}\" --environment Testing --UseInMemoryDatabase=true --InMemoryDatabaseName={inMemoryDbName} --webroot \"{webRootPath}\" --Jwt:SecretKey=super-secret-key-that-is-at-least-32-bytes-long-for-tests --Jwt:Issuer=App --Jwt:Audience=App --MediaStorage:UploadsPath=\"{_tempUploadsPath}\"",
            WorkingDirectory = Path.Combine(solutionDir, "API"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Testing";
        psi.EnvironmentVariables["UseInMemoryDatabase"] = "true";
        psi.EnvironmentVariables["InMemoryDatabaseName"] = inMemoryDbName;
        psi.EnvironmentVariables["MediaStorage__UploadsPath"] = _tempUploadsPath;

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ready = false;
        var maxAttempts = 40;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_process.HasExited)
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"API host process exited unexpectedly with code {_process.ExitCode}. Stderr: {stderr}"
                );
            }

            try
            {
                var response = await client.GetAsync($"{ServerUrl}/api/openapi/v1.json");
                if (response.IsSuccessStatusCode)
                {
                    ready = true;
                    break;
                }
            }
            catch
            {
                // Wait and retry
            }

            await Task.Delay(500);
        }

        if (!ready)
        {
            throw new TimeoutException(
                $"API server at '{ServerUrl}' did not become ready within 20 seconds."
            );
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (
                Directory.GetFiles(current, "*.sln*").Length > 0
                || Directory.Exists(Path.Combine(current, ".git"))
            )
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException(
            "Solution root could not be located from AppContext.BaseDirectory."
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch
            {
                // Best-effort cleanup
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        if (!string.IsNullOrEmpty(_tempUploadsPath) && Directory.Exists(_tempUploadsPath))
        {
            try
            {
                Directory.Delete(_tempUploadsPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
