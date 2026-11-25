using AppBlocks;
using AppBlocks.Authentication;
using AppBlocks.Connections;
using AppBlocks.InteractiveReports;
using AppBlocks.Documents;
using InteractiveReport.Client.FileDownload;
using InteractiveReport.Client.Json;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Cms.Web.Common.Hosting;
using UmbracoAppHost.Authentication;
using IdentityServer.Sso;
using DirectoryAccounts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using AppBlocks.InteractiveReports.Reports;
using UmbracoAppHost.Reports;
using AppBlocks.Blocks;
using AppBlocks.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Umbraco.Cms.Infrastructure.Migrations.Notifications;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Repositories;
using UmbracoAppHost.Application;
using AppBlocks.Attachments;
using AppBlocks.DataSources;
using AppBlocks.Documents.PaperReports;
using AppBlocks.Schedules;
using AppBlocks.Tabular;
using UmbracoAppHost.Hosting;
using uSync.BackOffice;
using uSync.BackOffice.Configuration;
using UmbracoAppHost.PeoplePicker;
using AppBlocks.AccountPickers;
using AppBlocks.Setup;

namespace UmbracoAppHost;

/// <summary>
/// The two calls a site makes to get everything the base provides: <see cref="AddUmbracoAppHost"/>
/// while services are registered and <see cref="UseUmbracoAppHostAsync"/> once the application is built.
/// </summary>
public static class UmbracoAppHostExtensions
{
    /// <summary>
    /// Initializes an independent site with the shared services, views and starter content, and the samples unless
    /// <c>UmbracoAppHost:Samples</c> is false.
    /// </summary>
    public static WebApplicationBuilder AddUmbracoAppHost(
        this WebApplicationBuilder builder, Action<AppBlocksBuilder>? configureConnections = null)
    {
        InstanceConfiguration.Apply(builder);
        builder.WebHost.UseStaticWebAssets();
        var samples = builder.Configuration.GetValue(SampleContent.Setting, true);
        if (samples) builder.Services.AddScoped<IFormResource, OrdersFormResource>();
        builder.CreateUmbracoBuilder()
            .AddBackOffice()
            .AddWebsite()
            .AddComposers()
            .AddUmbracoAppHost(builder.Configuration, connections =>
            {
                if (samples) connections.AddSampleOrders(builder.Services);
                configureConnections?.Invoke(connections);
            })
            .Build();
        return builder;
    }

    /// <summary>
    /// <see cref="AddUmbracoAppHost(WebApplicationBuilder, Action{AppBlocksBuilder}?)"/> with the host's own
    /// registrations: <typeparamref name="THost"/>'s services first, then its connections and
    /// <paramref name="configureConnections"/>.
    /// </summary>
    public static WebApplicationBuilder AddUmbracoAppHost<THost>(
        this WebApplicationBuilder builder, Action<AppBlocksBuilder>? configureConnections = null) where THost : IUmbracoAppHost
    {
        THost.ConfigureServices(builder);
        return builder.AddUmbracoAppHost(connections =>
        {
            THost.ConfigureConnections(builder, connections);
            configureConnections?.Invoke(connections);
        });
    }

    /// <summary>
    /// Adds the shared services: single sign-on and the account policy, accounts created from the
    /// directory, Interactive Reports with Umbraco-backed authorization and the built-in audit
    /// reports, configured accounts, the older setting names of break-glass access, virtual-path
    /// hosting, and each language's configured fallback for pages and values missing in its culture.
    /// On first boot the working uSync folder receives the library's export, without its sample content when
    /// <c>UmbracoAppHost:Samples</c> is false, then the host's own export from <c>UmbracoAppHost:HostExport</c>.
    /// Call it after <c>AddComposers()</c> so account provisioning runs after uSync's startup import.
    /// </summary>
    /// <param name="configureConnections">Registers the site's own connections, for example a sample database.</param>
    public static IUmbracoBuilder AddUmbracoAppHost(
        this IUmbracoBuilder builder,
        IConfiguration configuration,
        Action<AppBlocksBuilder>? configureConnections = null)
    {
        SharedViews.Configure(builder.Services);
        BlockPreviews.Configure(builder.Services);
        builder.Services.AddSingleton<BackOfficeAuthFiles>();
        Decorate<IBackOfficePathGenerator>(builder.Services, (paths, services) =>
            new InstallationBackOfficePaths(paths, services.GetRequiredService<BackOfficeAuthFiles>()));
        // Umbraco initializes publication state before uSync imports, so installation needs a
        // default language. Further languages and their settings come from the saved export.
        builder.Services.Configure<InstallDefaultDataSettings>(Umbraco.Cms.Core.Constants.Configuration.NamedOptions.InstallDefaultData.Languages,
            settings =>
            {
                if (configuration.GetSection("Umbraco:CMS:InstallDefaultData:Languages").Exists()) return;
                settings.InstallData = InstallDefaultDataOption.Values;
                settings.Values = new HashSet<string> { "en" };
            });
        builder.Services.AddOptions<uSyncSettings>().Configure<IWebHostEnvironment, ILoggerFactory, IEnumerable<UmbracoAppHost.Themes.ThemeLibrary>>((settings, environment, loggers, libraries) =>
        {
            settings.ImportOnFirstBoot = configuration.GetValue("uSync:Settings:ImportOnFirstBoot", true);
            settings.FirstBootGroup = configuration["uSync:Settings:FirstBootGroup"] ?? "All";
            var working = Path.GetFullPath(settings.RootFolder, environment.ContentRootPath);
            USyncFiles.EnsureWorkingCopy(working, configuration.GetValue(SampleContent.Setting, true),
                USyncFiles.HostExport(configuration, environment.ContentRootPath, working), loggers.CreateLogger(typeof(USyncFiles)), libraries);
            SharedViews.EnsureTemplates(environment, loggers.CreateLogger(typeof(SharedViews)), libraries);
        });
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(Umbraco.Cms.Web.Common.Controllers.RenderController).Assembly)
            .AddApplicationPart(typeof(UmbracoAppHostExtensions).Assembly);
        builder.Services.AddScoped<ReportAccessPolicy>();
        // Backoffice report management checks editors' permissions. The published website uses the member session.
        builder.Services.PostConfigure<WebsiteIdentityOptions>(options => options.IncludeBackOfficeUsers = false);
        builder.Services.AddScoped<IProtectedPageAdmission, EditorPageAdmission>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IReportAccessPolicy>(services => services.GetRequiredService<ReportAccessPolicy>()));
        builder.Services.AddScoped<BackOfficeMemberProvisioning>();
        builder.Services.AddScoped<IUserValidator<BackOfficeIdentityUser>, BackOfficeMemberProvisioning>();
        Decorate<IUserRepository>(builder.Services, (users, services) =>
            new MemberProvisioningUserRepository(users, services.GetRequiredService<IServiceScopeFactory>()));
        builder.AddNotificationHandler<UnattendedInstallNotification, InstalledAdministratorMember>();
        builder.AddNotificationHandler<DatabaseSchemaAndDataCreatedNotification, InstalledAdministratorMember>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, InstalledAdministratorMember>();
        builder.Services.Configure<ReportAccountSettings>(configuration.GetSection(ReportAccountSettings.SectionName));
        // Who is signed in, for the views and the application pages, and AppBlocks' access rule for those pages.
        builder.Services.AddScoped<SiteVisitor>();
        builder.Services.AddScoped<ApplicationAccessPolicy>();
        // The embedded identity server keeps its keys where this host always has: keys/identity-server under the
        // instance data folder, not the package's own default under App_Data.
        builder.Services.AddOptions<EmbeddedIdentityServerSettings>().PostConfigure<IWebHostEnvironment>((settings, environment) =>
        {
            if (!string.IsNullOrWhiteSpace(settings.DataPath)) return;
            var dataRoot = configuration["UmbracoAppHost:DataRoot"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "UmbracoAppHost");
            settings.DataPath = Path.Combine(dataRoot, "keys", "identity-server");
        });
        builder
            // Before the package's configured accounts, so a configured backoffice user can join this group on the first run.
            .AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ReportAdministratorsGroup>()
            .AddSingleSignOn(configuration)
            // The Add from directory package's composer also calls this; a second call does nothing.
            .AddAccountDirectory()
            .AddAppBlocksInteractiveReports()
            // After the companion, so a report seed that a callback configures comes after Interactive Reports' own configuration.
            .AddAppBlocks(appBlocks =>
            {
                // The two definitions the saved export keeps its own version of stay as the export imports them.
                appBlocks.SiteOwnsDefinition([.. AppBlocksDefinitions.HostOwned.Keys]);
                appBlocks.AllowInBlockGrid("Website: Page blocks", MemberSignInBlock.BlockAlias, "Page blocks");
                appBlocks.AllowInBlockGrid("Application: Application blocks", MemberSignInBlock.BlockAlias, "Page blocks");
                configureConnections?.Invoke(appBlocks);
            })
            // The configured groups exist before uSync's first-boot import resolves the export's group pickers.
            .AddNotificationAsyncHandler<uSyncImportStartingNotification, ConfiguredGroupsBeforeImport>()
            // AppBlocks' definitions exist before uSync imports what uses them, and only AppBlocks changes them.
            .AddNotificationAsyncHandler<uSyncImportStartingNotification, AppBlocksDefinitionImport>()
            .AddNotificationAsyncHandler<uSyncImportingItemNotification, AppBlocksDefinitionImport>()
            // After uSync's own startup work, which its composer registered: the build's definitions reach a site that an
            // earlier build created, and applications then hear that they may install their content.
            .AddNotificationAsyncHandler<UmbracoApplicationStartingNotification, SiteDefinitions>();
        builder.Services.AddSingleton<AppBlocksDefinitions>();
        builder.Services.AddSingleton(new AppBlocksSchema(typeof(PeoplePickerBlock).Assembly));
        builder.Services.AddSingleton<IAppBlockPlugin, PeoplePickerBlock>();
        builder.Services.AddSingleton<IAppBlockPlugin, MemberSignInBlock>();
        builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.TryAddSingleton<IPeoplePickerDirectory, PeoplePickerDirectory>();
        builder.Services.AddSingleton<PeoplePickerCache>();
        builder.Services.AddScoped<PeoplePickerService>();
        builder.Services.AddKeyedScoped<IAccountPickerProvider>(PeoplePickerBlock.AliasName, (services, _) => services.GetRequiredService<PeoplePickerService>());
        AddDirectoryAccountSettings(builder.Services, configuration);

        builder.AddAppBlocksDocuments();
        builder.Services.Replace(ServiceDescriptor.Scoped<IDocumentPresentationProvider, UmbracoAppHost.Themes.AppHostDocumentPresentation>());
        builder.Services.AddSingleton<UmbracoAppHost.Themes.SiteThemeCatalog>();
        // The authored menu with its visitor tokens replaced, once per request.
        builder.Services.AddScoped<UmbracoAppHost.Website.SiteMenu>();
        builder.Services.AddSingleton<Website.NavigationUpdates>();
        builder.AddNotificationHandler<ContentCacheRefresherNotification, Website.NavigationRefresh>();
        builder.AddNotificationHandler<PublicAccessCacheRefresherNotification, Website.NavigationRefresh>();
        builder.AddNotificationAsyncHandler<RoutingRequestNotification, UmbracoAppHost.Website.WebsiteTemplateInheritance>();
        AddCultureFallback(builder);

        InstallationCookies.Configure(builder.Services);
        // Use separate usernames for local login, including hosts using the IUmbracoBuilder entry point.
        builder.Services.Configure<SecuritySettings>(security =>
            security.UsernameIsEmail = configuration.GetValue("Umbraco:CMS:Security:UsernameIsEmail", false));

        // The AurorisNet.Umbraco.BreakGlass package's composer adds the configured administrator password and debug
        // sign-in. This host's older setting names still feed them.
        builder.AddBreakGlassCompatibility(configuration);
        return builder;
    }

    /// <summary>
    /// A culture whose language falls back to another serves that language's pages and values where its own are
    /// missing: under the culture's URLs, in its navigation and in property reads. See <see cref="Website.CultureFallback"/>.
    /// </summary>
    private static void AddCultureFallback(IUmbracoBuilder builder)
    {
        if (builder.Services.Any(service => service.ServiceType == typeof(Website.CultureFallback))) return;
        // The value fallback depends on the language settings alone: Umbraco's content cache needs it to build content.
        builder.Services.AddSingleton<Website.LanguageFallbacks>();
        builder.Services.AddSingleton<Website.CultureFallback>();
        builder.AddNotificationHandler<LanguageCacheRefresherNotification, Website.LanguageFallbacksRefresh>();
        Decorate<Umbraco.Cms.Core.Models.PublishedContent.IPublishedValueFallback>(builder.Services, (native, services) =>
            new Website.CultureValueFallback(native, services.GetRequiredService<Website.LanguageFallbacks>()));
        Decorate<Umbraco.Cms.Core.Services.Navigation.IPublishedContentStatusFilteringService>(builder.Services, (native, services) =>
            new Website.CultureFallbackStatusFilter(native, services.GetRequiredService<Website.CultureFallback>(),
                services.GetRequiredService<Umbraco.Cms.Core.Services.IPreviewService>(),
                services.GetRequiredService<Umbraco.Cms.Core.PublishedCache.IPublishedContentCache>()));
        builder.UrlProviders().InsertBefore<Umbraco.Cms.Core.Routing.DefaultUrlProvider, Website.CultureFallbackUrlProvider>();
        builder.ContentFinders().InsertAfter<Umbraco.Cms.Core.Routing.ContentFinderByUrl, Website.CultureFallbackContentFinder>();
    }

    /// <summary>Wraps the last registration of <typeparamref name="T"/>, keeping its lifetime.</summary>
    private static void Decorate<T>(IServiceCollection services, Func<T, IServiceProvider, T> decorate) where T : class
    {
        var registration = services.Last(service => service.ServiceType == typeof(T) && !service.IsKeyedService);
        services.Remove(registration);
        services.Add(ServiceDescriptor.Describe(typeof(T), provider =>
        {
            var native = registration.ImplementationInstance ?? registration.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!);
            return decorate((T)native, provider);
        }, registration.Lifetime));
    }

    /// <summary>
    /// Feeds the Add from directory package from this host's settings. The package reads the <c>Directory</c> section;
    /// this host's sites configure the identity server, its scheme names and the member type in <c>IdentityServer</c>
    /// and <c>Accounts</c>. Each value maps only when the <c>Directory</c> section leaves it unset, so a site can still
    /// set it there. Also registers this host's retirement and sign-in rules for the dialog.
    /// </summary>
    internal static void AddDirectoryAccountSettings(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AccountDirectorySettings>()
            .PostConfigure<IOptions<IdentityServerSettings>, IOptions<AccountsSettings>>((directory, identityServer, accounts) =>
            {
                var section = configuration.GetSection(AccountDirectorySettings.SectionName);
                var server = identityServer.Value;
                if (section[nameof(AccountDirectorySettings.IdentityServerAuthority)] is null)
                    directory.IdentityServerAuthority = server.Authority;
                if (section[nameof(AccountDirectorySettings.IdentityServerApiKey)] is null)
                    directory.IdentityServerApiKey = server.DirectoryApiKey;
                if (section[nameof(AccountDirectorySettings.IdentityServerAcceptAnyServerCertificate)] is null)
                    directory.IdentityServerAcceptAnyServerCertificate = server.AcceptAnyServerCertificate;
                if (section[nameof(AccountDirectorySettings.BackOfficeLoginProvider)] is null)
                    directory.BackOfficeLoginProvider = server.BackOfficeScheme;
                if (section[nameof(AccountDirectorySettings.MemberLoginProvider)] is null)
                    directory.MemberLoginProvider = server.MemberScheme;
                if (section[nameof(AccountDirectorySettings.MemberTypeAlias)] is null)
                    directory.MemberTypeAlias = accounts.Value.MemberTypeAlias;
            });
        // AddSingleton, not TryAdd: the package adds its defaults with TryAdd, and the last registration wins.
        services.AddSingleton<IRetiredAccountPolicy, AppHostRetiredAccountPolicy>();
        services.AddSingleton<ISignInLinkPolicy, AppHostSignInLinkPolicy>();
    }

    /// <summary>
    /// Boots Umbraco and builds the pipeline: the virtual path base, the workarounds in
    /// <c>Overrides/</c>, the backoffice and website middleware with report authentication, and the
    /// report endpoints under <c>/api/reports</c> and <c>/api/download</c>, and the form, paper report and
    /// attachment endpoints. Call it after
    /// <c>Build()</c> and before <c>RunAsync()</c>. The two callbacks add a site's own middleware
    /// and endpoints inside Umbraco's pipeline.
    /// </summary>
    public static async Task<WebApplication> UseUmbracoAppHostAsync(
        this WebApplication app,
        Action<IUmbracoApplicationBuilderContext>? configureMiddleware = null,
        Action<IUmbracoEndpointBuilderContext>? configureEndpoints = null)
    {
        if (app.Services.GetService<AppHostInstance>() is { } instance)
            app.Logger.LogInformation("Site data: {DataRoot}. Initial administrator credentials: {SettingsPath}",
                instance.Root, instance.SettingsPath);
        await app.BootUmbracoAsync();

        var cookies = app.Services.GetRequiredService<InstallationCookies>();
        app.Environment.WebRootFileProvider = new CompositeFileProvider(
            app.Services.GetRequiredService<BackOfficeAuthFiles>(), app.Environment.WebRootFileProvider);
        app.Use(cookies.Apply);
        app.UseUmbraco()
            .WithMiddleware(u =>
            {
                u.UseBackOffice();
                u.UseWebsite();
                configureMiddleware?.Invoke(u);
            })
            .WithEndpoints(u =>
            {
                u.UseBackOfficeEndpoints();
                u.UseWebsiteEndpoints();
                configureEndpoints?.Invoke(u);
            });

        return app;
    }

    /// <summary><see cref="UseUmbracoAppHostAsync"/> with <typeparamref name="THost"/>'s middleware and endpoints.</summary>
    public static Task<WebApplication> UseUmbracoAppHostAsync<THost>(this WebApplication app) where THost : IUmbracoAppHost
        => app.UseUmbracoAppHostAsync(THost.ConfigureMiddleware, THost.ConfigureEndpoints);
}
