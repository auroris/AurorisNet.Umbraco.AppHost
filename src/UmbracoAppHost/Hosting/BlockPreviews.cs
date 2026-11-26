using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Manifest;
using Umbraco.Community.BlockPreview;
using Umbraco.Community.BlockPreview.Controllers;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Hosting;

/// <summary>Razor previews for conventional blocks, alongside AppBlocks' own authoring views.</summary>
internal static class BlockPreviews
{
    public static void Configure(IServiceCollection services)
    {
        services.AddOptions<BlockPreviewOptions>()
            .Configure<IEnumerable<AppBlocksSchema>, IEnumerable<SiteTheme>, IOptions<HostingSettings>>((options, schemas, themes, hosting) =>
            {
                // These schemas and simple plugin registrations own their own authoring views.
                // Plugins with constructor dependencies are merged after boot (and on preview requests) to avoid DI cycles
                // while Umbraco constructs PropertyValueConverterCollection during unattended install.
                var aliases = schemas.DistinctBy(schema => schema.Assembly)
                    .SelectMany(schema => schema.Definitions("ContentTypes"))
                    .Where(definition => (bool?)definition.Element("Info")?.Element("IsElement") == true)
                    .Select(definition => (string)definition.Attribute("Alias")!)
                    .Concat(DirectPluginAliases(services))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var themeStylesheets = themes
                    .Select(theme => theme.Stylesheet)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var prefix = hosting.Value.ApplicationVirtualPath?.TrimEnd('/') ?? "";
                foreach (var settings in SettingsOf(options))
                {
                    settings.IgnoredContentTypes = [.. settings.IgnoredContentTypes.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase)];
                    var combined = (settings.Stylesheets ?? []).Concat(themeStylesheets).Distinct(StringComparer.OrdinalIgnoreCase);
                    settings.Stylesheets = combined.Select(path => PrefixPath(prefix, path)).ToList();
                }
            });
        services.AddSingleton<IPackageManifestReader, ThemePreviewStylesManifest>();
        services.Configure<MvcOptions>(options => options.Filters.Add(new EmptyBlockPreviewSelection()));
    }

    internal static void AddPluginAliases(BlockPreviewOptions options, IEnumerable<IAppBlockPlugin> plugins)
    {
        var aliases = plugins.Select(plugin => plugin.Alias).Where(alias => !string.IsNullOrWhiteSpace(alias)).ToArray();
        if (aliases.Length == 0) return;
        foreach (var settings in SettingsOf(options))
        {
            var merged = settings.IgnoredContentTypes.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (merged.Count != settings.IgnoredContentTypes.Count)
                settings.IgnoredContentTypes = merged;
        }
    }

    private static IEnumerable<string> DirectPluginAliases(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(IAppBlockPlugin) || descriptor.IsKeyedService) continue;
            if (descriptor.ImplementationInstance is IAppBlockPlugin instance)
            {
                if (!string.IsNullOrWhiteSpace(instance.Alias)) yield return instance.Alias;
            }
            else if (descriptor.ImplementationType is { IsAbstract: false } type && type.GetConstructor(Type.EmptyTypes) is { } ctor)
            {
                if (ctor.Invoke(null) is IAppBlockPlugin created && !string.IsNullOrWhiteSpace(created.Alias))
                    yield return created.Alias;
            }
        }
    }

    private static BlockTypeSettings[] SettingsOf(BlockPreviewOptions options)
        => [options.BlockGrid, options.BlockList, options.RichText, options.SingleBlock];

    internal sealed class BlockPreviewPluginAliases(IRuntimeState runtime, IOptions<BlockPreviewOptions> options, IServiceScopeFactory scopes)
        : INotificationHandler<UmbracoApplicationStartedNotification>
    {
        public void Handle(UmbracoApplicationStartedNotification notification)
        {
            if (runtime.Level != RuntimeLevel.Run) return;
            using var scope = scopes.CreateScope();
            AddPluginAliases(options.Value, scope.ServiceProvider.GetServices<IAppBlockPlugin>());
        }
    }

    internal static string PrefixPath(string prefix, string path)
        => prefix.Length > 0 && path.StartsWith('/') && !path.StartsWith("//")
            && path != prefix && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? prefix + path : path;

    /// <summary>Exposes registered <see cref="SiteTheme.Stylesheet"/> paths to AppBlocks' custom backoffice previews.</summary>
    private sealed class ThemePreviewStylesManifest(IEnumerable<SiteTheme> themes, IOptions<HostingSettings> hosting) : IPackageManifestReader
    {
        public Task<IEnumerable<PackageManifest>> ReadPackageManifestsAsync()
        {
            var prefix = hosting.Value.ApplicationVirtualPath?.TrimEnd('/') ?? "";
            var stylesheets = themes
                .Select(theme => theme.Stylesheet)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => PrefixPath(prefix, path!.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (stylesheets.Length == 0) return Task.FromResult<IEnumerable<PackageManifest>>([]);

            return Task.FromResult<IEnumerable<PackageManifest>>([new PackageManifest
            {
                Id = "UmbracoAppHost.PreviewThemeStyles",
                Name = "UmbracoAppHost theme preview styles",
                Version = "1.0.0",
                Extensions = [
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "appEntryPoint",
                        alias = "UmbracoAppHost.PreviewThemeStyles",
                        name = "Theme preview stylesheets",
                        js = "/App_Plugins/BlockPreviews.Host/entrypoint.js",
                        meta = new { stylesheets },
                    }),
                ],
            }]);
        }
    }

    // BlockPreview 6.3 returns an empty ContentTypes array when every element is excluded,
    // but its client interprets that array as a wildcard. Disable only that empty selection.
    private sealed class EmptyBlockPreviewSelection : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.Controller is not BlockPreviewApiController
                || context.Result is not ObjectResult { Value: BlockPreviewOptions options }) return;

            AddPluginAliases(options, context.HttpContext.RequestServices.GetServices<IAppBlockPlugin>());
            foreach (var settings in SettingsOf(options))
                if (settings.IgnoredContentTypes.Count > 0 && settings.ContentTypes?.Count == 0)
                    settings.Enabled = false;
        }

        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
