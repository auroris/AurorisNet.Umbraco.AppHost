using System.Security.Cryptography;
using System.Runtime.Versioning;
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

    [SupportedOSPlatform("windows")]
    internal static void CheckProfile(string pool, AppHostStartupReport report, ILogger logger)
    {
        // This check works even when the worker cannot create or read the instance key directory.
        try
        {
            var ciphertext = ProtectedData.Protect([1], null, DataProtectionScope.CurrentUser);
            ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException error)
        {
            logger.LogError(error, "Current-user DPAPI failed in IIS pool {Pool}", pool);
            report.AddProblem("Windows cannot protect keys for this site account.",
                "In IIS Manager, open Application Pools, select this site’s pool, open Advanced Settings and set Load User Profile to True. If it is already enabled, check setProfileEnvironment and whether Windows loaded the profile.");
        }
    }

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

    internal static void Check(WebApplication app, AppHostStartupReport report)
    {
        var iis = app.Services.GetRequiredService<IServer>().Features.Get<IIISEnvironmentFeature>();
        var routing = app.Services.GetRequiredService<IOptions<WebRoutingSettings>>().Value;
        var hosting = app.Services.GetRequiredService<IOptions<HostingSettings>>().Value;
        CheckAddresses(routing, hosting.ApplicationVirtualPath, app.Configuration["urls"], iis?.ApplicationVirtualPath, app.Logger, report);
        if (app.Services.GetService<AppHostInstance>() is not { } instance) return;

        try
        {
            CheckKeyRing(app.Services.GetRequiredService<IDataProtectionProvider>(), iis?.AppPoolId,
                Path.Combine(instance.Root, "data", "keys"), app.Logger);
        }
        catch (StartupFailure failure)
        {
            report.Record(failure);
        }
    }

    // Umbraco saves the unattended administrator's login before it protects the password-reset token.
    // Test the real ring as the worker first, so an unusable profile cannot leave a half-installed login.
    internal static void CheckKeyRing(IDataProtectionProvider provider, string? pool, string keyDirectory, ILogger logger)
    {
        try
        {
            var protector = provider.CreateProtector("UmbracoAppHost.StartupCheck");
            protector.Unprotect(protector.Protect("startup"));
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            var problem = new AppHostStartupProblem("The site could not use its sign-in and password-reset keys.",
                pool is null ? "Check the site account’s access to data/keys and its ability to decrypt the existing keys, then restart the site."
                    : "In IIS Manager, open Application Pools, select this site’s pool, and open Advanced Settings. Set Load User Profile to True. Give the pool identity Modify access to the instance’s data/keys folder. If it still fails, check setProfileEnvironment and whether Windows loaded the profile.");
            var worker = pool is null ? "the site process" : $"IIS pool '{pool}'";
            var message = $"Data Protection failed in {worker} at '{keyDirectory}' before Umbraco boot. {problem.Remedy}";
            logger.LogError(error, "{KeyRingFailure}", message);
            throw new StartupFailure(problem, message, error);
        }
    }

    internal static void CheckAddresses(WebRoutingSettings routing, string? virtualPath, string? bindings, string? iisPath, ILogger logger,
        AppHostStartupReport? report = null)
    {
        var address = PublicAddress(routing.UmbracoApplicationUrl);
        var path = "/" + virtualPath?.Trim().Trim('/');
        if (!string.IsNullOrWhiteSpace(routing.UmbracoApplicationUrl) && address is null)
        {
            logger.LogWarning("{Setting} needs an absolute HTTP or HTTPS site address including any application path, without credentials, query or fragment.", ApplicationUrl);
            report?.AddAdvice("Umbraco’s public site address is invalid.", $"Set {ApplicationUrl} in instance.json to the full HTTP or HTTPS address, including the IIS application path.");
        }
        else if (address is null && routing.ApplicationUrlDetection == ApplicationUrlDetection.None)
        {
            if (iisPath is not null) logger.LogWarning("Umbraco application URL detection is disabled. Set {Setting} to the full public address including the IIS application path so Umbraco can generate absolute links.", ApplicationUrl);
            report?.AddAdvice("Umbraco needs its public site address to generate links.", $"Set {ApplicationUrl} in instance.json to the address people use, including the IIS application path. Check any explicit Umbraco:CMS:Hosting:ApplicationVirtualPath override.");
        }
        if (address is not null && address.AbsolutePath.TrimEnd('/') != path.TrimEnd('/'))
            logger.LogWarning("{PathSetting} overrides the path in {UrlSetting}. If both refer to the same mount, remove the explicit virtual path.", VirtualPathHosting.SettingKey, ApplicationUrl);
        if (iisPath is not null && !string.Equals(iisPath.TrimEnd('/'), path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("IIS mounts this application at {IisPath}, but Umbraco uses {VirtualPath}. For direct IIS hosting set {Setting} to the full public address including the IIS path and check any explicit virtual-path override. Incorrect paths can give 404s and a blank backoffice.", iisPath, path, ApplicationUrl);
            if (address is not null || routing.ApplicationUrlDetection != ApplicationUrlDetection.None)
                report?.AddAdvice("IIS and Umbraco use different application paths.", $"If IIS serves the site directly, set {ApplicationUrl} to its full address, including the application path. Check Umbraco:CMS:Hosting:ApplicationVirtualPath for an override.");
        }
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
