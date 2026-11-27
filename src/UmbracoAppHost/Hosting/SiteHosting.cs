using System.Security.Cryptography;
using AurorisNet.Umbraco.ApplicationVirtualPathFix;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;

namespace UmbracoAppHost.Hosting;

internal static class SiteHosting
{
    internal const string ApplicationUrl = "Umbraco:CMS:WebRouting:UmbracoApplicationUrl";

    private static Uri? PublicAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri : null;

    // Also expose the path through IConfiguration: application installers read it when writing content links.
    internal static void ApplyVirtualPath(IConfiguration configuration)
    {
        if (configuration[VirtualPathHosting.SettingKey] is not null ||
            PublicAddress(configuration[ApplicationUrl]) is not { } address) return;
        var path = address.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return;
        if (configuration is IConfigurationManager manager)
            manager.AddInMemoryCollection(new Dictionary<string, string?> { [VirtualPathHosting.SettingKey] = path });
        else
            configuration[VirtualPathHosting.SettingKey] = path;
    }

    internal static void Check(WebApplication app)
    {
        var iis = app.Services.GetRequiredService<IServer>().Features.Get<IIISEnvironmentFeature>();
        var routing = app.Services.GetRequiredService<IOptions<WebRoutingSettings>>().Value;
        var hosting = app.Services.GetRequiredService<IOptions<HostingSettings>>().Value;
        CheckAddresses(routing, hosting.ApplicationVirtualPath, app.Configuration["urls"], iis?.ApplicationVirtualPath, app.Logger);
        if (iis is null || !OperatingSystem.IsWindows() || app.Services.GetService<AppHostInstance>() is not { } instance) return;

        CheckKeyRing(app.Services.GetRequiredService<IDataProtectionProvider>(), iis.AppPoolId,
            Path.Combine(instance.Root, "data", "keys"), app.Logger);
    }

    // Umbraco saves the unattended administrator's login before it protects the password-reset token.
    // Test the real ring as the worker first, so an unusable profile cannot leave a half-installed login.
    internal static void CheckKeyRing(IDataProtectionProvider provider, string pool, string keyDirectory, ILogger logger)
    {
        try
        {
            var protector = provider.CreateProtector("UmbracoAppHost.StartupCheck");
            protector.Unprotect(protector.Protect("startup"));
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            var message = $"Data Protection failed in IIS pool '{pool}' before Umbraco boot. Verify that the pool identity can modify '{keyDirectory}', "
                + "that Load User Profile and setProfileEnvironment are enabled, and that Windows loaded the identity's profile. "
                + "Sign-in and password resets need this key ring.";
            logger.LogError(error, "{KeyRingFailure}", message);
            throw new InvalidOperationException(message, error);
        }
    }

    internal static void CheckAddresses(WebRoutingSettings routing, string? virtualPath, string? bindings, string? iisPath, ILogger logger)
    {
        var address = PublicAddress(routing.UmbracoApplicationUrl);
        var path = "/" + virtualPath?.Trim().Trim('/');
        if (!string.IsNullOrWhiteSpace(routing.UmbracoApplicationUrl) && address is null)
            logger.LogWarning("{Setting} needs an absolute HTTP or HTTPS site address including any application path, without credentials, query or fragment.", ApplicationUrl);
        else if (iisPath is not null && address is null && routing.ApplicationUrlDetection == ApplicationUrlDetection.None)
            logger.LogWarning("Umbraco application URL detection is disabled. Set {Setting} to the full public address including the IIS application path so Umbraco can generate absolute links.", ApplicationUrl);
        if (address is not null && address.AbsolutePath.TrimEnd('/') != path.TrimEnd('/'))
            logger.LogWarning("{PathSetting} overrides the path in {UrlSetting}. If both refer to the same mount, remove the explicit virtual path.", VirtualPathHosting.SettingKey, ApplicationUrl);
        if (iisPath is not null && !string.Equals(iisPath.TrimEnd('/'), path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("IIS mounts this application at {IisPath}, but Umbraco uses {VirtualPath}. For direct IIS hosting set {Setting} to the full public address including the IIS path and check any explicit virtual-path override. Incorrect paths can give 404s and a blank backoffice.", iisPath, path, ApplicationUrl);
        foreach (var binding in (bindings ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var endpoint = BindingAddress.Parse(binding);
                if (endpoint.PathBase.Length == 0) continue;
                logger.LogWarning("Urls contains a listener address with a path. Use a Kestrel listener address without the application path and put the public URL in {Setting}. IIS supplies its own binding.", ApplicationUrl);
            }
            catch (FormatException)
            {
                logger.LogWarning("Urls contains an invalid listener address. Check the semicolon-separated Kestrel bindings. IIS supplies its own binding.");
            }
            break;
        }
    }
}
