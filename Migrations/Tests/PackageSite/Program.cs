using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration["InstanceRoot"] = Path.Combine(builder.Environment.ContentRootPath, "instance");
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.StartAsync();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var runtime = app.Services.GetRequiredService<IRuntimeState>();
    var routing = app.Services.GetRequiredService<IContentRoutingReadiness>();
    while (runtime.Level != RuntimeLevel.BootFailed && (runtime.Level != RuntimeLevel.Run || !routing.IsReady))
        await Task.Delay(100, timeout.Token);
    if (runtime.Level != RuntimeLevel.Run)
        throw new InvalidOperationException($"Umbraco finished startup at {runtime.Level}.", runtime.BootFailedException);

    using var scope = app.Services.CreateScope();
    var types = scope.ServiceProvider.GetRequiredService<IContentTypeService>();
    foreach (var alias in new[] { "appForm", "appReport", "paperReport", "webPage" })
        if (types.Get(alias) is null) throw new InvalidOperationException($"Missing installed content type {alias}.");

    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    var backoffice = await client.GetStringAsync("/umbraco/", timeout.Token);
    if (!backoffice.Contains("importmap", StringComparison.Ordinal)) throw new InvalidOperationException("Missing backoffice import map.");
    foreach (var plugin in new[] { "ApplicationBlocks", "AppBlocks.InteractiveReports", "AppBlocks.Documents" })
    {
        var manifest = await client.GetFromJsonAsync<JsonElement>($"/App_Plugins/{plugin}/umbraco-package.json", timeout.Token);
        if (manifest.GetProperty("extensions").GetArrayLength() == 0) throw new InvalidOperationException($"Empty {plugin} manifest.");
    }
    Console.WriteLine("Installed the AppHost and AppBlocks content types and served all three AppBlocks plugin manifests.");
}
finally
{
    await app.StopAsync();
}
