using AurorisNet.Umbraco.DisableTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Api.Management.Services.NewsDashboard;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.BackgroundJobs.Jobs;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class PackageIntegrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/portal")]
    [ResetPublishedContentServices]
    public async Task Host_discovers_telemetry_suppression_and_serves_packaged_report_assets(string prefix)
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
        }, TestContext.Current.CancellationToken);

        var jobs = site.Services.GetServices<IRecurringBackgroundJob>().ToArray();
        Assert.DoesNotContain(jobs, job => job is ReportSiteJob);
        Assert.Contains(jobs, job => job is TempFileCleanupJob);

        var package = typeof(DisableTelemetryComposer).Assembly;
        Assert.Equal(package, site.Services.GetRequiredService<IInstallationService>().GetType().Assembly);
        var news = site.Services.GetRequiredService<INewsDashboardService>();
        Assert.Equal(package, news.GetType().Assembly);
        Assert.Empty((await news.GetItemsAsync()).Items);

        using var browser = site.CreateClient();
        foreach (var asset in new[] { "ir.js", "ir-admin.js", "ir-chart.js" })
        {
            using var response = await browser.GetAsync(prefix + "/api/reports/ui/" + asset, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{asset}: {response.StatusCode}");
            Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
    }
}
