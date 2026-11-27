using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class AppHostStartupTests
{
    [Theory]
    [InlineData("lock", "instance lock")]
    [InlineData("update", "update-site.ps1")]
    [InlineData("settings", "Check instance.json")]
    [InlineData("application-settings", "appsettings JSON")]
    [InlineData("media", "the media folder")]
    [InlineData("views", "Views folder")]
    [InlineData("runtime", "Umbraco:CMS:Global:UseHttps")]
    [InlineData("cms-database", "Umbraco could not open its CMS database")]
    [InlineData("host-check", "Configure the case database")]
    [InlineData("exception", "host log")]
    public async Task Failure_serves_a_standalone_page_before_installation(string fault, string remedy)
    {
        var root = Directory.CreateTempSubdirectory("apphost-diagnostic-").FullName;
        var instance = Path.Combine(root, "instance");
        var state = Directory.CreateDirectory(Path.Combine(instance, ".apphost")).FullName;
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            using var held = fault == "lock" ? File.Open(Path.Combine(state, "run.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None) : null;
            if (fault == "update") Directory.CreateDirectory(Path.Combine(state, "pending-update"));
            if (fault == "settings") File.WriteAllText(Path.Combine(instance, "instance.json"), "{");
            if (fault == "application-settings") File.WriteAllText(Path.Combine(root, "appsettings.json"), "{");
            if (fault == "media") File.WriteAllText(Path.Combine(instance, "media"), "in the way");
            if (fault == "views") File.WriteAllText(Path.Combine(root, "Views"), "in the way");
            if (fault == "cms-database") File.WriteAllText(Path.Combine(root, "broken.db"), "invalid SQLite database");

            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var address = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}";
            port.Stop();
            var run = AppHostStartup.RunAsync<CaseSite>(["--contentRoot", root, "--InstanceRoot", instance, "--urls", address,
                "--applicationName", typeof(Program).Assembly.GetName().Name!, "--fault", fault,
                .. (fault == "runtime" ? new[] { "--Umbraco:CMS:Runtime:Mode", "Production" } : Array.Empty<string>()),
                .. (fault == "cms-database" ? new[] { "--ConnectionStrings:umbracoDbDSN", $"Data Source={Path.Combine(root, "broken.db")}",
                    "--ConnectionStrings:umbracoDbDSN_ProviderName", "Microsoft.Data.Sqlite" } : Array.Empty<string>())], stopped.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                foreach (var path in new[] { "/", "/portal/umbraco/" })
                {
                    using var response = await GetWhenListening(client, path, run, timeout.Token);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    Assert.True(response.Headers.CacheControl?.NoStore);
                    var page = await response.Content.ReadAsStringAsync(timeout.Token);
                    Assert.Contains(remedy, page);
                    Assert.DoesNotContain(root, page);
                    Assert.DoesNotContain("private-diagnostic-detail", page);
                    if (fault != "cms-database") Assert.DoesNotContain("Umbraco could not open its CMS database", page);
                    if (fault is "host-check" or "media")
                    {
                        Assert.Contains("&lt;dbo&gt;", page);
                        Assert.Contains("Set the second database", page);
                    }
                }
                Assert.False(File.Exists(Path.Combine(instance, "data", "Umbraco.sqlite.db")));
                if (fault == "host-check")
                {
                    Assert.True(File.Exists(Path.Combine(root, "check-ran")));
                    using var released = File.Open(Path.Combine(state, "run.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            finally
            {
                await stopped.CancelAsync();
                await run;
            }
        }
        finally
        {
            await Serilog.Log.CloseAndFlushAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> GetWhenListening(HttpClient client, string path, Task run, CancellationToken token)
    {
        while (true)
        {
            if (run.IsCompleted) await run;
            try { return await client.GetAsync(path, token); }
            catch (HttpRequestException) { await Task.Delay(100, token); }
        }
    }

    private abstract class CaseSite : IUmbracoAppHost
    {
        public static void ConfigureServices(WebApplicationBuilder builder)
        {
            builder.Services.AddAppHostStartupCheck<CaseDatabaseCheck>();
            builder.Services.AddHostedService<MustNotStart>();
        }
    }

    private sealed class CaseDatabaseCheck(IConfiguration configuration, IHostEnvironment environment) : IAppHostStartupCheck
    {
        public async Task CheckAsync(AppHostStartupReport report, CancellationToken cancellationToken)
        {
            if (configuration["fault"] == "exception") throw new InvalidOperationException("private-diagnostic-detail");
            await File.WriteAllTextAsync(Path.Combine(environment.ContentRootPath, "check-ran"), "", cancellationToken);
            report.AddProblem("The case database uses the wrong schema.", "Configure the case database to use <dbo>.");
            report.AddProblem("A second database is missing.", "Set the second database in instance.json.");
        }
    }

    private sealed class MustNotStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Site hosted services started.");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
