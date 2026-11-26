using System.Text.Json;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Infrastructure.Manifest;

namespace UmbracoAppHost;

/// <summary>
/// Full branding for the backoffice login screen: logo, panel image, optional backdrop image, bilingual greetings,
/// and an optional configuration section (with <c>Greeting</c>, <c>FrenchGreeting</c>, <c>BackdropImage</c>) whose
/// values override these defaults.
/// </summary>
public sealed record LoginBranding(
    string LogoImage,
    string PanelImage,
    string? BackdropImage = null,
    string? Greeting = null,
    string? FrenchGreeting = null,
    string? ConfigurationSection = null);

/// <summary>
/// Branding for the backoffice login screen. A site without any gets Umbraco's own login screen.
/// </summary>
/// <remarks>
/// A site or theme library ships the logo and the panel image as static web assets under
/// <c>wwwroot/umbraco</c>, where Umbraco resolves its login images, and points the three login image
/// settings at them here. A site that configures its own under <c>Umbraco:CMS:Content</c> keeps them,
/// so a theme can brand a site by default and the site can still override.
/// </remarks>
public static class LoginScreen
{
    /// <param name="logoImage">The logo, relative to <c>wwwroot/umbraco</c>, for both login logo settings.</param>
    /// <param name="panelImage">The panel image beside the sign-in form, relative to <c>wwwroot/umbraco</c>.</param>
    public static IServiceCollection AddLoginScreen(this IServiceCollection services, IConfiguration configuration, string logoImage, string panelImage)
    {
        var configured = configuration.GetSection("Umbraco:CMS:Content");
        return services.Configure<ContentSettings>(content =>
        {
            if (string.IsNullOrEmpty(configured["LoginLogoImage"]))
                content.LoginLogoImage = logoImage;
            if (string.IsNullOrEmpty(configured["LoginLogoImageAlternative"]))
                content.LoginLogoImageAlternative = logoImage;
            if (string.IsNullOrEmpty(configured["LoginBackgroundImage"]))
                content.LoginBackgroundImage = panelImage;
        });
    }

    /// <summary>
    /// Configures the backoffice login images and publishes the public login manifest (optional backdrop image,
    /// bilingual greetings and Shadow DOM layout styling) through <see cref="IPackageManifestReader"/>.
    /// </summary>
    public static IServiceCollection AddLoginScreen(this IServiceCollection services, IConfiguration configuration, LoginBranding branding)
    {
        ArgumentNullException.ThrowIfNull(branding);
        services.AddLoginScreen(configuration, branding.LogoImage, branding.PanelImage);
        services.AddSingleton(branding);
        if (!services.Any(d => d.ServiceType == typeof(IPackageManifestReader) && d.ImplementationType == typeof(AppHostLoginManifest)))
            services.AddSingleton<IPackageManifestReader, AppHostLoginManifest>();
        return services;
    }
}

internal sealed class AppHostLoginManifest(IConfiguration configuration, IEnumerable<LoginBranding> registrations) : IPackageManifestReader
{
    public Task<IEnumerable<PackageManifest>> ReadPackageManifestsAsync()
    {
        var branding = registrations.LastOrDefault();
        if (branding is null) return Task.FromResult<IEnumerable<PackageManifest>>([]);

        var section = string.IsNullOrWhiteSpace(branding.ConfigurationSection) ? null : configuration.GetSection(branding.ConfigurationSection);
        string? Setting(string key) => section?[key]?.Trim() is { Length: > 0 } value ? value : null;

        var greeting = Setting("Greeting") ?? branding.Greeting;
        var french = Setting("FrenchGreeting") ?? Setting("Greeting") ?? branding.FrenchGreeting ?? branding.Greeting;
        var backdrop = Setting("BackdropImage") ?? branding.BackdropImage;

        var extensions = new List<object>();
        if (!string.IsNullOrWhiteSpace(greeting))
        {
            extensions.Add(Localization("EnUS", "en-US", greeting));
            extensions.Add(Localization("EnGB", "en-GB", greeting));
        }
        if (!string.IsNullOrWhiteSpace(french))
            extensions.Add(Localization("FrFR", "fr-FR", french));

        extensions.Add(JsonSerializer.SerializeToElement(new
        {
            type = "appEntryPoint",
            alias = "UmbracoAppHost.LoginScreen",
            name = "Backoffice login appearance",
            js = "/App_Plugins/UmbracoAppHost/login-screen.js",
            meta = new { backdropImage = backdrop },
        }));

        return Task.FromResult<IEnumerable<PackageManifest>>([new PackageManifest
        {
            Id = "UmbracoAppHost.Login",
            Name = "UmbracoAppHost login screen",
            Version = "1.0.0",
            AllowPublicAccess = true,
            Extensions = [.. extensions],
        }]);
    }

    private static JsonElement Localization(string aliasSuffix, string culture, string greeting) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "localization",
            alias = "UmbracoAppHost.Login.Localization." + aliasSuffix,
            name = $"Backoffice login greeting ({culture})",
            meta = new
            {
                culture,
                // Umbraco selects one key per day of the week.
                localizations = new { login = Enumerable.Range(0, 7).ToDictionary(day => $"greeting{day}", _ => greeting) },
            },
        });
}
