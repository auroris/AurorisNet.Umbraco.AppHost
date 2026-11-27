using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Application;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.InteractiveReports.Reports;

namespace UmbracoAppHost.Tests;

/// <summary>
/// A saved view shows its report's rows, so on this host, whose policy replaces AppBlocks' own, reading, changing or deleting one
/// still asks whether the caller may read the report now.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class SavedViewAccessTests
{
    private const string Report = "pilots-only-orders";

    [Fact]
    [ResetPublishedContentServices]
    public async Task Saved_views_of_a_specific_groups_report_follow_who_may_read_the_report()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly }, TestContext.Current.CancellationToken);
        var pilot = site.CreateMember("view-pilot", "view-pilot@example.invalid", "View pilot", "Pilots");
        var outsider = site.CreateMember("view-outsider", "view-outsider@example.invalid", "View outsider", "Passengers");
        using (var scope = site.CreateScope())
        {
            var services = scope.ServiceProvider;
            var pilots = (await services.GetRequiredService<IMemberGroupService>().GetByNameAsync("Pilots"))!;
            var contents = services.GetRequiredService<IContentService>();
            var node = contents.Create(Report, ApplicationStarter.DataSourceRootKey, PublishedReportCatalog.ReportAlias);
            node.SetValue("alias", Report); node.SetValue("connection", SampleReport.Connection);
            node.SetValue("sql", "SELECT ORDER_ID, CUSTOMER FROM ORDERS");
            node.SetValue("access", PublishedReportCatalog.SpecificGroups); node.SetValue("accessGroups", pilots.Key.ToString());
            Assert.True(contents.Save(node).Success);
#pragma warning disable CS0618
            var published = contents.Publish(node, ["*"]);
#pragma warning restore CS0618
            Assert.True(published.Success, string.Join("; ", published.EventMessages?.GetAll().Select(m => m.Message) ?? []));
        }
        using var piloting = await site.SignInMemberAsync(pilot);
        using var outside = await site.SignInMemberAsync(outsider);
        using var administrator = site.CreateClient();
        (await administrator.GetAsync($"{site.PathBase}/__tests/login/{Constants.Security.SuperUserKey}?scheme=backoffice&name=Views", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var member = site.PathBase + "/api/reports";
        var backOffice = site.PathBase + InteractiveReportsSite.BackOfficeReportsRoute;

        var own = await Save(piloting, member, "Pilot's view", isGlobal: false);
        var shared = await Save(administrator, backOffice, "Everyone's view", isGlobal: true);
        foreach (var id in new[] { own, shared })
            Assert.Equal(HttpStatusCode.OK, (await piloting.GetAsync($"{member}/{Report}/{id}", TestContext.Current.CancellationToken)).StatusCode);
        // A member outside the report's groups reads none of its views, the shared one included.
        Assert.False((await outside.GetAsync($"{member}/{Report}/{shared}", TestContext.Current.CancellationToken)).IsSuccessStatusCode);

        // A pilot removed from the group keeps no way back in through the views they saved.
        using (var scope = site.CreateScope())
            scope.ServiceProvider.GetRequiredService<IMemberService>().DissociateRoles([pilot.Username], ["Pilots"]);
        Assert.False((await piloting.GetAsync($"{member}/{Report}/schema", TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        foreach (var id in new[] { own, shared })
            Assert.False((await piloting.GetAsync($"{member}/{Report}/{id}", TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        Assert.False((await piloting.PutAsJsonAsync($"{member}/{own}", new { title = "Renamed by the pilot" }, TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        Assert.False((await piloting.DeleteAsync($"{member}/{own}", TestContext.Current.CancellationToken)).IsSuccessStatusCode);

        // An administrator still reads, changes and removes them.
        Assert.Equal(HttpStatusCode.OK, (await administrator.GetAsync($"{backOffice}/{Report}/{own}", TestContext.Current.CancellationToken)).StatusCode);
        using (var renamed = await administrator.PutAsJsonAsync($"{backOffice}/{own}", new { title = "Renamed by an administrator" }, TestContext.Current.CancellationToken))
            Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"{backOffice}/{own}", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static async Task<long> Save(HttpClient client, string route, string title, bool isGlobal)
    {
        using var response = await client.PostAsJsonAsync($"{route}/{Report}/saved", new { title, isGlobal, state = new { page = new { index = 1, size = 10 } } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{route}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body).GetProperty("id").GetInt64();
    }
}
