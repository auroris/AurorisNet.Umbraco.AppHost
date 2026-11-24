using AppBlocks.Setup;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Services;
using uSync.BackOffice;
using UmbracoAppHost.Hosting;
using UmbracoAppHost.Testing;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Tests;

/// <summary>A site across builds: each start brings it the definitions of the build it runs, once, before the application installs its content.</summary>
[Collection("Umbraco hosts")]
public sealed class SiteDefinitionsTests
{
    private static readonly Guid ApplicationGrid = Guid.Parse("97b36051-4abe-e750-6667-9cb7ee6a9a99");

    /// <summary>What the starts of one site did: each import uSync began, and the release note the site held when its definitions were ready.</summary>
    private sealed class StartLog
    {
        public int Imports { get; set; }
        public List<string?> Ready { get; } = [];
        public string? FailWith { get; set; }
    }

    /// <summary>Stands in for an application: it reads a dictionary item of the host's export when the definitions are ready, as an installer would use them.</summary>
    private sealed class Application(StartLog log, IDictionaryItemService dictionary)
        : INotificationAsyncHandler<uSyncImportStartingNotification>, INotificationAsyncHandler<DefinitionsReadyNotification>
    {
        public Task HandleAsync(uSyncImportStartingNotification notification, CancellationToken cancellationToken)
        {
            log.Imports++;
            return Task.CompletedTask;
        }

        public async Task HandleAsync(DefinitionsReadyNotification notification, CancellationToken cancellationToken)
        {
            log.Ready.Add((await dictionary.GetAsync("release-note"))?.Translations.FirstOrDefault(translation => translation.LanguageIsoCode == "en")?.Value);
            if (log.FailWith is { } message) throw new InvalidOperationException(message);
        }
    }

    [Fact]
    public async Task A_new_site_whose_application_cannot_install_its_content_does_not_come_up()
    {
        // A new site finishes installing after the web server has started, so the failure reaches the start that waits for it.
        var log = new StartLog { FailWith = "The application's first installation failed." };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            UseInstallSnapshot = false,
            FirstBootGroup = "Settings",
            InstallTimeout = TimeSpan.FromSeconds(90),
            ConfigureBuilder = builder =>
            {
                builder.Services.AddSingleton(log);
                builder.Services.AddTransient<INotificationAsyncHandler<DefinitionsReadyNotification>, Application>();
            },
        }, TestContext.Current.CancellationToken));
        Assert.Single(log.Ready);
        Assert.Contains("The application's first installation failed.", error.ToString());
    }

    [Fact]
    public async Task A_site_takes_each_builds_definitions_once_and_before_the_application_installs_its_content()
    {
        var root = Directory.CreateTempSubdirectory("apphost-definitions-").FullName;
        // The host's own export, as a host project's uSync folder is: one dictionary item a release changes.
        var export = Path.Combine(root, "host-export");
        Directory.CreateDirectory(Path.Combine(export, "Dictionary"));
        void Ship(string note) => File.WriteAllText(Path.Combine(export, "Dictionary", "release-note.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Dictionary Key="6f0f1a52-5a5e-4c1b-9a3e-0d1f7b0c2a11" Alias="release-note" Level="0">
              <Info />
              <Translations>
                <Translation Language="en">{note}</Translation>
              </Translations>
            </Dictionary>
            """);
        var log = new StartLog();
        Task<TestSite> StartAsync() => TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            Root = root,
            FirstBootGroup = "Settings",
            Settings = { [USyncFiles.HostExportSetting] = export },
            ConfigureBuilder = builder =>
            {
                builder.Services.AddSingleton(log);
                builder.Services.AddTransient<INotificationAsyncHandler<uSyncImportStartingNotification>, Application>();
                builder.Services.AddTransient<INotificationAsyncHandler<DefinitionsReadyNotification>, Application>();
            },
        }, TestContext.Current.CancellationToken);
        static string Shipped(TestSite site, string export, IServiceProvider services)
            => USyncFiles.Fingerprint(USyncFiles.SharedDefinitions(export, site.USyncFolder, services.GetServices<ThemeLibrary>()));

        try
        {
            // A new site: after the first boot, which imports the dictionary only with content, the host imports the shared
            // definitions and records them, and the application hears that they are ready once they are there.
            Ship("First release");
            string first;
            await using (var site = await StartAsync())
            {
                using var scope = site.CreateScope();
                var services = scope.ServiceProvider;
                Assert.Equal(2, log.Imports);
                Assert.Equal(["First release"], log.Ready);
                first = Shipped(site, export, services);
                Assert.Equal(first, services.GetRequiredService<IKeyValueService>().GetValue(SiteDefinitions.RecordKey));

                // AppBlocks' definitions are recorded as the build has them, and the grid the export keeps its own version of is the export's.
                var installer = services.GetRequiredService<SchemaInstaller>();
                Assert.All((await installer.StatusAsync(TestContext.Current.CancellationToken)).Packages, package => Assert.Equal((0, 0), (package.Changed.Count, package.Unrecorded)));
                var dataTypes = services.GetRequiredService<IDataTypeService>();
                Assert.Equal("Application: Application blocks", (await dataTypes.GetAsync(ApplicationGrid))!.Name);

                // As if an earlier build had installed other definitions of the Form and of that grid, and someone renamed both since.
                var keyValues = services.GetRequiredService<IKeyValueService>();
                var types = services.GetRequiredService<IContentTypeService>();
                var form = types.Get("appForm")!;
                keyValues.SetValue(SchemaInstaller.RecordKey(form.Key), new string('0', 64));
                keyValues.SetValue(SchemaInstaller.RecordKey(ApplicationGrid), new string('0', 64));
                form.Name = "Site form";
                Assert.True((await types.UpdateAsync(form, Constants.Security.SuperUserKey)).Success);
                var grid = (await dataTypes.GetAsync(ApplicationGrid))!;
                grid.Name = "Site grid";
                Assert.True((await dataTypes.UpdateAsync(grid, Constants.Security.SuperUserKey)).Success);
            }

            // The same build again imports nothing. The start resets the AppBlocks definition the build changed, and leaves
            // the grid to the site, whose export owns it.
            await using (var site = await StartAsync())
            {
                using var scope = site.CreateScope();
                var services = scope.ServiceProvider;
                Assert.Equal(2, log.Imports);
                Assert.Equal(["First release", "First release"], log.Ready);
                Assert.Equal("Form", services.GetRequiredService<IContentTypeService>().Get("appForm")!.Name);
                Assert.Equal("Site grid", (await services.GetRequiredService<IDataTypeService>().GetAsync(ApplicationGrid))!.Name);
            }

            // A build that ships another definition: the first start writes it to the working folder and imports the shared
            // definitions before the application is told, and records them. The import also brings back the export's grid.
            Ship("Second release");
            // uSync's export on save can leave a second file for an item it renamed, here the grid under AppBlocks' name for it.
            // uSync refuses to import a folder with two files for one key, so the start removes the one the build does not ship.
            var working = Path.Combine(root, "instance", "uSync");
            var leftover = Path.Combine(working, "DataTypes", "AppBlocksFormsContainer.config");
            File.Copy(Path.Combine(working, "DataTypes", "ApplicationApplicationBlocks.config"), leftover);
            await using (var site = await StartAsync())
            {
                Assert.False(File.Exists(leftover));
                using var scope = site.CreateScope();
                var services = scope.ServiceProvider;
                Assert.Equal(3, log.Imports);
                Assert.Equal("Second release", log.Ready[^1]);
                var second = Shipped(site, export, services);
                Assert.NotEqual(first, second);
                Assert.Equal(second, services.GetRequiredService<IKeyValueService>().GetValue(SiteDefinitions.RecordKey));
                Assert.Contains("Second release", File.ReadAllText(Path.Combine(site.USyncFolder, "Dictionary", "release-note.config")));
                Assert.Equal("Application: Application blocks", (await services.GetRequiredService<IDataTypeService>().GetAsync(ApplicationGrid))!.Name);
            }

            // An application whose installation fails stops the start.
            log.FailWith = "The application's content step failed.";
            var stopped = await Assert.ThrowsAnyAsync<Exception>(StartAsync);
            Assert.Contains("The application's content step failed.", stopped.ToString());
            log.FailWith = null;

            // A definition that cannot be imported stops the start too, before the application is told, and the next start imports again.
            var ready = log.Ready.Count;
            Directory.CreateDirectory(Path.Combine(export, "Languages"));
            var broken = Path.Combine(export, "Languages", "broken.config");
            File.WriteAllText(broken, """
                <?xml version="1.0" encoding="utf-8"?>
                <Language Key="0c1d2e3f-4a5b-4c6d-8e7f-90a1b2c3d4e5" Alias="not a culture" Level="0">
                  <Name>Broken</Name>
                  <IsoCode>not a culture</IsoCode>
                  <IsMandatory>false</IsMandatory>
                  <IsDefault>false</IsDefault>
                </Language>
                """);
            var refused = await Assert.ThrowsAnyAsync<Exception>(StartAsync);
            Assert.Contains("could not be imported", refused.ToString());
            Assert.Equal(ready, log.Ready.Count);
            // The refused build's files are out of the working folder again, so the corrected build starts from what the site had.
            Assert.False(File.Exists(Path.Combine(root, "instance", "uSync", "Languages", "broken.config")));
            File.Delete(broken);
            Ship("Third release");
            await using (var site = await StartAsync())
            {
                Assert.Equal("Third release", log.Ready[^1]);
                Assert.False(File.Exists(Path.Combine(site.USyncFolder, "Languages", "broken.config")));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
