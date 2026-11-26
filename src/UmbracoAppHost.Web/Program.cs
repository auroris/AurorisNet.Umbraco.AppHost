using AppBlocks.Connections;
using AppBlocks.Forms;
using UmbracoAppHost;
using UmbracoAppHost.Application;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost<Program>();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync<Program>();
await app.RunAsync();

public partial class Program : IUmbracoAppHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
        => builder.Services.AddScoped<IFormResource, OrdersFormResource>();

    public static void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections)
        => connections.AddSampleOrders(builder.Services);
}
