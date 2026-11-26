using System.Xml.Linq;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

public sealed class SampleContentTests
{
    /// <summary>The Data sources root's own children that only the samples use; the root and the Welcome report are every site's.</summary>
    private static readonly string[] SampleDataSources =
        ["/DataSources/Orders", "/DataSources/Orderslines", "/DataSources/OrdersPrintRows", "/DataSources/Ordersreport", "/DataSources/Orderregionchoices", "/DataSources/Orderstatuschoices",
            "/DataSources/Ordersschedulerows", "/DataSources/Ordersschedulebars", "/DataSources/Ordersschedule"];

    [Fact]
    public void The_library_export_ships_no_sample_content_and_the_sample_host_ships_all_of_it()
    {
        var libraryFiles = Directory.EnumerateFiles(Path.Combine(ApplicationStarter.Export, "Content"), "*.config").Select(XElement.Load).ToList();
        Assert.All(libraryFiles, file => Assert.False(SampleContent.IsSample(file), file.Element("Info")!.Element("Path")!.Value));
        Assert.Equal(["/DataSources", "/DataSources/Recentlychangedpagesreport"], libraryFiles.Select(file => file.Element("Info")!.Element("Path")!.Value).Order(StringComparer.Ordinal));

        var sampleFiles = Directory.EnumerateFiles(Path.Combine(ApplicationStarter.SampleExport, "Content"), "*.config").Select(XElement.Load).ToList();
        static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        Assert.All(sampleFiles, file =>
        {
            var path = file.Element("Info")!.Element("Path")!.Value;
            Assert.True(Under(path, "/SampleApplication") || Under(path, "/WebsiteThemeDemo") || SampleDataSources.Contains(path), path);
            Assert.True(SampleContent.IsSample(file), path);
        });
        Assert.Equal(sampleFiles.Select(file => Guid.Parse(file.Attribute("Key")!.Value)).Order(), SampleContent.Keys.Order());
    }
}
