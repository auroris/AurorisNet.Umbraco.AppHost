using BreakGlass;
using IdentityServer.Sso;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Keeps the settings that sites used before break-glass access moved to the <c>AurorisNet.Umbraco.BreakGlass</c>
/// package: <c>UmbracoAppHost:AdminPassword</c>, <c>AdminUserName</c> and <c>AdminEmail</c>, and the
/// <c>BackOfficeLogin</c> Debug flow, which turned on debug sign-in. The <c>BreakGlass</c> names are preferred.
/// </summary>
internal static class BreakGlassCompatibility
{
    public const string LegacyAdminPasswordKey = "UmbracoAppHost:AdminPassword";
    public const string LegacyAdminUserNameKey = "UmbracoAppHost:AdminUserName";
    public const string LegacyAdminEmailKey = "UmbracoAppHost:AdminEmail";

    /// <summary>The address this host has always given the built-in administrator when no setting names one.</summary>
    public const string DefaultAdminEmail = "admin@localhost.local";

    /// <summary>
    /// Fills the package's settings from the older keys too. It runs whenever the package reads its settings, so a
    /// saved file applies through either name.
    /// </summary>
    public static IUmbracoBuilder AddBreakGlassCompatibility(this IUmbracoBuilder builder, IConfiguration configuration)
    {
        builder.Services.PostConfigure<BreakGlassSettings>(settings => Apply(settings, configuration));
        return builder;
    }

    internal static void Apply(BreakGlassSettings settings, IConfiguration configuration)
    {
        if (Find(configuration, BreakGlassSettings.AdminPasswordKey, LegacyAdminPasswordKey, out var password))
            settings.AdminPassword = password;
        if (Find(configuration, BreakGlassSettings.AdminUserNameKey, LegacyAdminUserNameKey, out var username))
            settings.AdminUserName = username;
        // Hosts using the IUmbracoBuilder entry point have no defaults.json, which also names this address.
        settings.AdminEmail = Find(configuration, BreakGlassSettings.AdminEmailKey, LegacyAdminEmailKey, out var email)
            ? email : DefaultAdminEmail;
        // An explicit BreakGlass:DebugSignIn decides. Otherwise, as before the package, debug sign-in follows the
        // login flow selected for debug mode; the package still requires Umbraco:CMS:Hosting:Debug either way.
        if (!Find(configuration, BreakGlassSettings.DebugSignInKey, null, out _))
            settings.DebugSignIn = DebugFlowSelected(configuration);
    }

    private static bool DebugFlowSelected(IConfiguration configuration)
    {
        try
        {
            var login = configuration.GetSection(BackOfficeLoginSettings.SectionName).Get<BackOfficeLoginSettings>() ?? new BackOfficeLoginSettings();
            return login.GetEffectiveFlow(debug: true) == BackOfficeLoginFlow.Debug;
        }
        // An unknown flow stops the site at startup through BackOfficeLogin's own validation. Until then, and after
        // such a value is saved into a running site, debug sign-in stays off.
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The value from the configuration source with the highest precedence that sets either key, preferring the new
    /// key within one source. A host's appsettings.json that still uses the old name therefore overrides a default
    /// that uses the new one, as it would if both had the same name.
    /// </summary>
    private static bool Find(IConfiguration configuration, string key, string? legacyKey, out string? value)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
                if (provider.TryGet(key, out value) || legacyKey is not null && provider.TryGet(legacyKey, out value))
                    return true;
            value = null;
            return false;
        }
        value = configuration[key] ?? (legacyKey is null ? null : configuration[legacyKey]);
        return value is not null;
    }
}
