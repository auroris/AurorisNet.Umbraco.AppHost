using System.Text.Json;
using AppBlocks.DataSources;
using AppBlocks.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Infrastructure.Manifest;
using UmbracoAppHost.Application;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.PeoplePicker;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Tests;

public sealed class Wave2PrimitivesTests
{
    [Fact]
    public async Task LoginBranding_publishes_bilingual_manifest_with_config_overrides()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CustomSite:Login:Greeting"] = "Configured Portal",
            ["CustomSite:Login:BackdropImage"] = "portal/override.avif",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLoginScreen(configuration, new LoginBranding(
            LogoImage: "portal/logo.svg",
            PanelImage: "portal/panel.jpg",
            BackdropImage: "portal/default.avif",
            Greeting: "Default Portal",
            FrenchGreeting: "Portail par défaut",
            ConfigurationSection: "CustomSite:Login"));

        await using var provider = services.BuildServiceProvider();
        var content = provider.GetRequiredService<IOptions<ContentSettings>>().Value;
        Assert.Equal("portal/logo.svg", content.LoginLogoImage);
        Assert.Equal("portal/logo.svg", content.LoginLogoImageAlternative);
        Assert.Equal("portal/panel.jpg", content.LoginBackgroundImage);

        var reader = Assert.Single(provider.GetServices<IPackageManifestReader>());
        var manifest = Assert.Single(await reader.ReadPackageManifestsAsync());
        Assert.True(manifest.AllowPublicAccess);
        Assert.Equal("UmbracoAppHost.Login", manifest.Id);
        var extensions = manifest.Extensions.OfType<JsonElement>().ToArray();
        Assert.Contains(extensions, ext => ext.GetProperty("alias").GetString() == "UmbracoAppHost.LoginScreen"
            && ext.GetProperty("meta").GetProperty("backdropImage").GetString() == "portal/override.avif");
        Assert.Contains(extensions, ext => ext.GetProperty("alias").GetString() == "UmbracoAppHost.Login.Localization.EnUS"
            && ext.GetProperty("meta").GetProperty("localizations").GetProperty("login").GetProperty("greeting0").GetString() == "Configured Portal");
        Assert.Contains(extensions, ext => ext.GetProperty("alias").GetString() == "UmbracoAppHost.Login.Localization.FrFR"
            && ext.GetProperty("meta").GetProperty("localizations").GetProperty("login").GetProperty("greeting6").GetString() == "Configured Portal");
    }

    [Fact]
    public void AddSiteTheme_registers_theme_embedded_library_schema_and_login_branding()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        var theme = new SiteTheme(
            Alias: "custom",
            PageLayout: "CustomMaster.cshtml",
            SignInLayout: "CustomSignIn.cshtml",
            PrintStylesheet: "/custom/print.css",
            Assembly: typeof(UmbracoAppHostExtensions).Assembly,
            Stylesheet: "/custom/site.css",
            Login: new LoginBranding("custom/logo.svg", "custom/panel.jpg"));

        services.AddSiteTheme(configuration, theme);

        using var provider = services.BuildServiceProvider();
        Assert.Same(theme, provider.GetServices<SiteTheme>().Single());
        Assert.Equal(typeof(UmbracoAppHostExtensions).Assembly, provider.GetServices<ThemeLibrary>().Single().Assembly);
        Assert.Equal(typeof(UmbracoAppHostExtensions).Assembly, provider.GetServices<AppBlocksSchema>().Single().Assembly);
        Assert.Single(provider.GetServices<LoginBranding>());
    }

    [Fact]
    public void ApplicationPages_Key_is_deterministic_and_scoped()
    {
        var a1 = ApplicationPages.Key("Portal", "home");
        var a2 = ApplicationPages.Key("Portal", "home");
        var b = ApplicationPages.Key("OtherPortal", "home");
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void SignedInMemberAccess_registers_custom_rule_types_and_member_only_pages()
    {
        var key = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSignedInMemberRuleType("PortalSignedInMember");
        services.RequireMemberOnlyAccess(key);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ProtectedPageAdmissionOptions>>().Value;
        Assert.Contains(SignedInMemberAccess.RuleType, options.SignedInMemberRuleTypes);
        Assert.Contains("PortalSignedInMember", options.SignedInMemberRuleTypes);
        Assert.Contains(key, options.MemberOnlyPageKeys);
    }

    [Fact]
    public async Task FakeDirectory_and_TestClock_behave_deterministically()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock { Now = fixedTime };
        Assert.Equal(fixedTime, clock.GetUtcNow());

        var person = FakeDirectory.Person("S-1-5-21-100", "Alex Example", "alex@example.invalid",
            new Dictionary<string, string> { ["employeeNumber"] = "E12345" });
        var directory = new FakeDirectory { Offline = false, Results = [person] };
        var settings = new PeoplePickerSettings();

        var found = await directory.SearchAsync(settings, ["Alex"], TestContext.Current.CancellationToken);
        Assert.Single(found);
        Assert.Equal("S-1-5-21-100", found[0].Sid);

        var selected = await directory.FindAsync(settings, "S-1-5-21-100", TestContext.Current.CancellationToken);
        Assert.NotNull(selected);
        Assert.Equal(1, directory.SelectionChecks);
        Assert.Equal("E12345", selected.Attributes!["employeeNumber"]);
    }
}
