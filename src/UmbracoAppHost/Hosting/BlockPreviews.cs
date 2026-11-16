using AppBlocks.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Community.BlockPreview;
using Umbraco.Community.BlockPreview.Controllers;

namespace UmbracoAppHost.Hosting;

/// <summary>Razor previews for conventional blocks, alongside AppBlocks' own authoring views.</summary>
internal static class BlockPreviews
{
    public static void Configure(IServiceCollection services)
    {
        services.AddOptions<BlockPreviewOptions>()
            .Configure<IEnumerable<AppBlocksSchema>, IOptions<HostingSettings>>((options, schemas, hosting) =>
            {
                // These schemas own the existing form, field, report and native layout previews.
                var aliases = schemas.DistinctBy(schema => schema.Assembly)
                    .SelectMany(schema => schema.Definitions("ContentTypes"))
                    .Where(definition => (bool?)definition.Element("Info")?.Element("IsElement") == true)
                    .Select(definition => (string)definition.Attribute("Alias")!)
                    .ToArray();
                var prefix = hosting.Value.ApplicationVirtualPath?.TrimEnd('/') ?? "";
                foreach (var settings in new[] { options.BlockGrid, options.BlockList, options.RichText, options.SingleBlock })
                {
                    settings.IgnoredContentTypes = [.. settings.IgnoredContentTypes.Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase)];
                    settings.Stylesheets = settings.Stylesheets?.Select(path =>
                        prefix.Length > 0 && path.StartsWith('/') && !path.StartsWith("//")
                            && path != prefix && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                            ? prefix + path : path).ToList();
                }
            });
        services.Configure<MvcOptions>(options => options.Filters.Add(new EmptyBlockPreviewSelection()));
    }

    // BlockPreview 6.3 returns an empty ContentTypes array when every element is excluded,
    // but its client interprets that array as a wildcard. Disable only that empty selection.
    private sealed class EmptyBlockPreviewSelection : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.Controller is not BlockPreviewApiController
                || context.Result is not ObjectResult { Value: BlockPreviewOptions options }) return;

            foreach (var settings in new[] { options.BlockGrid, options.BlockList, options.RichText, options.SingleBlock })
                if (settings.IgnoredContentTypes.Count > 0 && settings.ContentTypes?.Count == 0)
                    settings.Enabled = false;
        }

        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
