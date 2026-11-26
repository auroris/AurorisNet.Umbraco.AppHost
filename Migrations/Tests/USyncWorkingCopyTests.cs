using Microsoft.Extensions.Logging.Abstractions;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

public sealed class USyncWorkingCopyTests
{
    [Fact]
    public async Task Interrupted_and_overlapping_copies_preserve_site_files_and_finish_the_export()
    {
        var root = Directory.CreateTempSubdirectory("usync-copy-");
        try
        {
            var working = Path.Combine(root.FullName, "working");
            var export = Path.Combine(root.FullName, "export");
            var marker = Path.Combine(working, "usync.config");
            void Copy() => USyncFiles.EnsureWorkingCopy(working, export, NullLogger.Instance);

            // The host export is unavailable on the first attempt, after the packaged files were copied.
            Assert.Throws<DirectoryNotFoundException>(Copy);
            Assert.False(File.Exists(marker));
            var siteFile = Path.Combine(working, "Templates", "websitehome.config");
            Assert.True(File.Exists(siteFile));
            File.WriteAllText(siteFile, "<site-edit />");

            Directory.CreateDirectory(export);
            File.WriteAllText(Path.Combine(export, "host.config"), "<host />");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Copy)));

            Assert.Equal("<site-edit />", File.ReadAllText(siteFile));
            Assert.Equal("<host />", File.ReadAllText(Path.Combine(working, "host.config")));
            Assert.Equal("uSync", System.Xml.Linq.XElement.Load(marker).Name.LocalName);
        }
        finally { root.Delete(recursive: true); }
    }
}
