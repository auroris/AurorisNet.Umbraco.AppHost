using AppBlocks.Connections;
using AppBlocks.Forms;
using UmbracoAppHost;
using UmbracoAppHost.Application;

await AppHostStartup.RunAsync<Program>(args);

public partial class Program : IUmbracoAppHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
        => builder.Services.AddScoped<IFormResource, OrdersFormResource>();

    public static void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections)
        => connections.AddSampleOrders(builder.Services);
}
