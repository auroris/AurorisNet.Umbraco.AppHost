using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.IIS;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost;

public static class AppHostStartup
{
    /// <summary>Starts a managed site, serving a standalone diagnostic page if setup or pre-boot checks fail.</summary>
    public static async Task RunAsync<THost>(string[] args, CancellationToken cancellationToken = default)
        where THost : IUmbracoAppHost
    {
        WebApplicationBuilder? builder = null;
        AppHostInstance? instance = null;
        WebApplication? app = null;
        var report = new AppHostStartupReport();
        try
        {
            try
            {
                builder = WebApplication.CreateBuilder(args);
                UmbracoAppHostExtensions.AddManagedSite<THost>(builder, null, out instance, report);
                app = builder.Build();
                using var registration = cancellationToken.Register(app.Lifetime.StopApplication);
                await app.UseUmbracoAppHostAsync<THost>();
                var runtime = app.Services.GetRequiredService<Umbraco.Cms.Core.Services.IRuntimeState>();
                if (runtime.Level == Umbraco.Cms.Core.RuntimeLevel.BootFailed)
                    throw new InvalidOperationException("Umbraco reported a boot failure.", runtime.BootFailedException);
            }
            catch (Exception error) when (error is not (OperationCanceledException or HostAbortedException)
                && !cancellationToken.IsCancellationRequested)
            {
                if (app is null || error is StartupFailure) instance?.Dispose();
                // Disposing an in-process IIS server stops native callbacks for the worker.
                // Keep the unstarted host alive until the independent responder has finished.
                await ServeFailureAsync(builder, args, report, error, app is null, cancellationToken);
                return;
            }
            await app.StartAsync(cancellationToken);
            await app.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            if (app is not null) await app.DisposeAsync();
            instance?.Dispose();
        }
    }

    private static async Task ServeFailureAsync(WebApplicationBuilder? original, string[] args, AppHostStartupReport report,
        Exception error, bool setupIncomplete, CancellationToken token)
    {
        IReadOnlyList<AppHostStartupProblem> problems = error is StartupFailure failure ? failure.Problems
            : [original is null && error is InvalidDataException
                ? new AppHostStartupProblem("The application settings could not be loaded.", "Check the site's appsettings JSON files, then restart the site.")
                : new AppHostStartupProblem("The site failed during startup.", "Check the host log for the failed operation, correct the problem, then restart the site.")];

        // Avoid loading a broken settings file or starting any services registered by Umbraco or the site.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = original?.Environment.ContentRootPath });
        builder.Configuration.AddEnvironmentVariables("ASPNETCORE_").AddEnvironmentVariables("DOTNET_")
            .AddEnvironmentVariables().AddCommandLine(args);
        if (original is not null) builder.Configuration.AddConfiguration(original.Configuration);
        builder.WebHost.UseKestrel(options => options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false))
            .UseIIS().UseIISIntegration();
        builder.Services.AddRoutingCore();
        builder.Host.UseConsoleLifetime();
        builder.Logging.AddSimpleConsole();
        if (OperatingSystem.IsWindows()) builder.Logging.AddEventLog();
        await using var diagnostic = builder.Build();
        diagnostic.Logger.LogError(error, "AppHost startup stopped");
        if (setupIncomplete && OperatingSystem.IsWindows()
            && diagnostic.Services.GetRequiredService<IServer>().Features.Get<IIISEnvironmentFeature>() is { } iis)
            SiteHosting.CheckProfile(iis.AppPoolId, report, diagnostic.Logger);
        var advice = report.Advisories.Count > 0 ? $"<h2>Also check</h2>{RenderSteps(report.Advisories)}" : "";
        var remaining = setupIncomplete ? "<p>AppHost could not finish loading this installation. Restart after the repairs below to check its remaining requirements.</p>" : "";
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Site startup stopped</title>
            <style>body{font:1rem/1.55 system-ui,sans-serif;max-width:44rem;margin:6vh auto;padding:0 1.5rem;color:#202631}h1{font-size:2rem}li{margin:1.25rem 0}li p{margin:.25rem 0;white-space:pre-wrap;overflow-wrap:anywhere}</style>
            </head><body><main><h1>Site startup stopped</h1>
            {{remaining}}<h2>How to fix it</h2>{{RenderSteps([.. problems, .. report.Problems])}}{{advice}}
            <p>After making these changes, restart the site. In IIS Manager, stop its application pool, wait for the worker to exit, then start the pool. Refresh this page to check again.</p>
            </main></body></html>
            """;
        diagnostic.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "text/html; charset=utf-8";
            return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : context.Response.WriteAsync(html);
        });
        await diagnostic.StartAsync(token);
        await diagnostic.WaitForShutdownAsync(token);
    }

    private static string RenderSteps(IReadOnlyList<AppHostStartupProblem> problems)
        => "<ol>" + string.Concat(problems.Distinct().Select(p =>
            $"<li><strong>{HtmlEncoder.Default.Encode(p.Description)}</strong><p>{HtmlEncoder.Default.Encode(p.Remedy)}</p></li>")) + "</ol>";
}
