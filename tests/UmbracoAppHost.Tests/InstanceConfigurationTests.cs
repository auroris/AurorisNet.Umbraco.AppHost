using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using BreakGlass;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Hosting;

namespace UmbracoAppHost.Tests;

/// <summary>
/// The configuration an instance starts with: where AppBlocks keeps its files, so an existing instance keeps finding
/// them, and the break-glass settings that the host's defaults and the older setting names produce.
/// </summary>
public sealed class InstanceConfigurationTests
{
    [Fact]
    public void An_interrupted_update_blocks_startup_until_the_files_are_recovered()
    {
        var root = Directory.CreateTempSubdirectory("pending-update-").FullName;
        try
        {
            var pending = Directory.CreateDirectory(Path.Combine(root, "instance", ".apphost", "pending-update"));
            var failure = Assert.Throws<InvalidOperationException>(() => Apply(root, configured: null));
            Assert.Contains("update-site.ps1", failure.Message);
            Assert.False(File.Exists(Path.Combine(root, "instance", "instance.json")));
            pending.Delete();
            Apply(root, configured: null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AppBlocks_keeps_its_files_in_the_instance_data_folder()
    {
        var root = Directory.CreateTempSubdirectory("instance-config-").FullName;
        var configuration = Apply(root, configured: null);
        Assert.Equal(Path.Combine(root, "instance", "data"), configuration["AppBlocks:DataRoot"]);
        Assert.Equal(configuration["UmbracoAppHost:DataRoot"], configuration["AppBlocks:DataRoot"]);
    }

    [Fact]
    public void A_host_that_configured_the_AppBlocks_folder_keeps_it()
    {
        var root = Directory.CreateTempSubdirectory("instance-config-").FullName;
        var configured = Path.Combine(root, "appblocks");
        var configuration = Apply(root, configured);
        Assert.Equal(configured, configuration["AppBlocks:DataRoot"]);
        Assert.Equal(Path.Combine(root, "instance", "data"), configuration["UmbracoAppHost:DataRoot"]);
    }

    [Fact]
    public void Defaults_name_the_administrator_and_keep_debug_sign_in_off_without_opt_in()
    {
        var settings = BreakGlassSettingsFor();
        Assert.Null(settings.AdminPassword);
        Assert.Equal("admin", settings.GetAdminUserName());
        Assert.Equal("admin@localhost.local", settings.GetAdminEmail());
        Assert.False(settings.IsDebugSignInActive(hostingDebug: true));
        Assert.False(settings.IsDebugSignInActive(hostingDebug: false));
    }

    [Fact]
    public void The_reference_website_settings_still_configure_the_administrator()
    {
        // The website's appsettings.json uses the older names: an empty password, admin and its local address.
        var settings = BreakGlassSettingsFor(new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build()
            .AsEnumerable().ToDictionary());
        Assert.Equal("", settings.AdminPassword);
        Assert.Equal("admin", settings.GetAdminUserName());
        Assert.Equal("admin@localhost.local", settings.GetAdminEmail());
        Assert.False(settings.IsDebugSignInActive(hostingDebug: true));
    }

    [Fact]
    public void An_older_name_in_a_later_source_overrides_a_newer_name_in_an_earlier_one()
    {
        // The host's settings come after the library's defaults, which use the BreakGlass names.
        var cleared = BreakGlassSettingsFor(new Dictionary<string, string?> { ["UmbracoAppHost:AdminUserName"] = "", ["UmbracoAppHost:AdminEmail"] = "" });
        Assert.Null(cleared.GetAdminUserName());
        Assert.Null(cleared.GetAdminEmail());

        var older = BreakGlassSettingsFor(new() { [BreakGlassSettings.AdminPasswordKey] = "Newer-name-in-defaults91!" },
            new() { ["UmbracoAppHost:AdminPassword"] = "Older-name-in-site91!" });
        Assert.Equal("Older-name-in-site91!", older.AdminPassword);
        var newer = BreakGlassSettingsFor(new() { ["UmbracoAppHost:AdminPassword"] = "Older-name-in-defaults91!" },
            new() { [BreakGlassSettings.AdminPasswordKey] = "Newer-name-in-site91!" });
        Assert.Equal("Newer-name-in-site91!", newer.AdminPassword);
        // Within one source the BreakGlass name wins.
        var both = BreakGlassSettingsFor(new Dictionary<string, string?>
        {
            ["UmbracoAppHost:AdminEmail"] = "older@example.invalid", [BreakGlassSettings.AdminEmailKey] = "newer@example.invalid",
        });
        Assert.Equal("newer@example.invalid", both.GetAdminEmail());
    }

    [Theory]
    [InlineData("Form", null, false)]
    [InlineData("Auto", null, false)]
    [InlineData("Debug", null, true)]
    [InlineData("Form", "true", true)]
    [InlineData("Debug", "false", false)]
    public void The_debug_login_flow_turns_on_debug_sign_in_unless_BreakGlass_says_otherwise(string debugFlow, string? debugSignIn, bool active)
    {
        var layer = new Dictionary<string, string?> { ["BackOfficeLogin:DebugFlow"] = debugFlow };
        if (debugSignIn is not null) layer[BreakGlassSettings.DebugSignInKey] = debugSignIn;
        var settings = BreakGlassSettingsFor(layer);
        Assert.Equal(active, settings.IsDebugSignInActive(hostingDebug: true));
        Assert.False(settings.IsDebugSignInActive(hostingDebug: false));
    }

    /// <summary>The BreakGlass settings an instance reads, with each dictionary as one configuration source after the defaults.</summary>
    private static BreakGlassSettings BreakGlassSettingsFor(params Dictionary<string, string?>[] layers)
    {
        var root = Directory.CreateTempSubdirectory("instance-config-").FullName;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["InstanceRoot"] = Path.Combine(root, "instance") });
        foreach (var layer in layers) builder.Configuration.AddInMemoryCollection(layer);
        InstanceConfiguration.Apply(builder);
        using (var services = builder.Services.BuildServiceProvider())
            services.GetRequiredService<AppHostInstance>().Dispose();
        var settings = builder.Configuration.GetSection(BreakGlassSettings.SectionName).Get<BreakGlassSettings>() ?? new BreakGlassSettings();
        BreakGlassCompatibility.Apply(settings, builder.Configuration);
        return settings;
    }

    private static IConfiguration Apply(string root, string? configured)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["AppBlocks:DataRoot"] = configured,
        });
        InstanceConfiguration.Apply(builder);
        // Release the instance lease; only the configuration is under test.
        using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<AppHostInstance>().Dispose();
        return builder.Configuration;
    }
}
