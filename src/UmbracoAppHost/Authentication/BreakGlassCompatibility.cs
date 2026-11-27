using BreakGlass;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Keeps the settings that sites used before break-glass access moved to the <c>AurorisNet.Umbraco.BreakGlass</c>
/// package: <c>UmbracoAppHost:AdminPassword</c>, <c>AdminUserName</c> and <c>AdminEmail</c>.
/// </summary>
internal static class BreakGlassCompatibility
{
    public const string LegacyAdminPasswordKey = "UmbracoAppHost:AdminPassword";
    public const string LegacyAdminUserNameKey = "UmbracoAppHost:AdminUserName";
    public const string LegacyAdminEmailKey = "UmbracoAppHost:AdminEmail";

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
        if (Find(configuration, BreakGlassSettings.AdminEmailKey, LegacyAdminEmailKey, out var email))
            settings.AdminEmail = email;
    }

    /// <summary>
    /// The value from the configuration source with the highest precedence that sets either key, preferring the new
    /// key within one source. A host's appsettings.json that still uses the old name therefore overrides a default
    /// that uses the new one, as it would if both had the same name.
    /// </summary>
    private static bool Find(IConfiguration configuration, string key, string legacyKey, out string? value)
    {
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
                if (provider.TryGet(key, out value) || provider.TryGet(legacyKey, out value))
                    return true;
            value = null;
            return false;
        }
        value = configuration[key] ?? configuration[legacyKey];
        return value is not null;
    }
}
