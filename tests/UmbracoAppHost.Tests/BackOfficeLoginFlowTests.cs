using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Security;
using BreakGlass;
using UmbracoAppHost.Authentication;
using IdentityServer.Sso;

namespace UmbracoAppHost.Tests;

public sealed class BackOfficeLoginFlowTests
{
    [Theory]
    [InlineData(false, "Form", "Debug", null, false)]
    [InlineData(false, "Auto", "Debug", "Umbraco.Domain", false)]
    [InlineData(false, "IdentityServer", "Debug", "Umbraco.Domain", false)]
    [InlineData(false, "Debug", "Debug", null, false)]
    [InlineData(true, "Form", "Debug", null, true)]
    [InlineData(true, "Auto", "Debug", null, true)]
    [InlineData(true, "Form", "Form", null, false)]
    [InlineData(true, "Form", "IdentityServer", "Umbraco.Domain", false)]
    public async Task Configuration_selects_one_initial_provider_and_gates_the_password_bypass(
        bool debug, string defaultFlow, string? debugFlow, string? redirectScheme, bool acceptsAnyPassword)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackOfficeLogin:DefaultFlow"] = defaultFlow,
            ["BackOfficeLogin:DebugFlow"] = debugFlow,
        }).Build();
        var login = Options.Create(configuration.GetSection("BackOfficeLogin").Get<BackOfficeLoginSettings>()!);
        var hosting = Options.Create(new HostingSettings { Debug = debug });
        var identityServer = Options.Create(new IdentityServerSettings { Enabled = true, Name = "Domain" });
        var reader = new SsoPackageManifestReader(new SsoProviderCatalog(identityServer), login, hosting, identityServer);
        var manifest = Assert.Single(await reader.ReadPackageManifestsAsync());
        var extensions = manifest.Extensions!.Cast<JsonElement>().ToArray();
        Assert.Single(extensions);
        Assert.True(extensions[0].GetProperty("meta").GetProperty("linking").GetProperty("allowManualLinking").GetBoolean());
        var automatic = extensions.Where(extension => extension.GetProperty("meta").GetProperty("behavior").GetProperty("autoRedirect").GetBoolean()).ToArray();
        if (redirectScheme is null) Assert.Empty(automatic);
        else Assert.Equal(redirectScheme, Assert.Single(automatic).GetProperty("forProviderName").GetString());

        // The BreakGlass package accepts any password; this host's settings decide whether it may.
        var breakGlass = new BreakGlassSettings();
        BreakGlassCompatibility.Apply(breakGlass, configuration);
        Assert.Equal(acceptsAnyPassword, breakGlass.IsDebugSignInActive(debug));
    }

    [Theory]
    [InlineData(true, "Form", "true", true)]
    [InlineData(true, "IdentityServer", "true", true)]
    [InlineData(true, "Debug", "false", false)]
    [InlineData(false, "Debug", "true", false)]
    [InlineData(true, null, null, true)]
    public void BreakGlass_DebugSignIn_overrides_the_login_flow_but_never_debug_mode(
        bool debug, string? debugFlow, string? debugSignIn, bool active)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackOfficeLogin:DebugFlow"] = debugFlow,
            [BreakGlassSettings.DebugSignInKey] = debugSignIn,
        }.Where(setting => setting.Value is not null)).Build();
        var settings = configuration.GetSection(BreakGlassSettings.SectionName).Get<BreakGlassSettings>() ?? new BreakGlassSettings();
        BreakGlassCompatibility.Apply(settings, configuration);
        Assert.Equal(active, settings.IsDebugSignInActive(debug));
    }
}
