using AppBlocks.Setup;

namespace UmbracoAppHost.Hosting;

public sealed partial class SiteDefinitions
{
    // Register the content patches implemented in transition folders in release order.
    private IReadOnlyList<ContentStep> ContentUpgrades => [];
}
