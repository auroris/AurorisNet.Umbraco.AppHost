using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Constants = Umbraco.Cms.Core.Constants;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Community.BlockPreview;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class BlockPreviewTests
{
    private const string HeadlineAlias = "testPreviewHeadlineBlock";
    private const string LayoutAlias = "testPreviewTwoColumnLayoutBlock";
    private static readonly Guid HeadlineTypeKey = Guid.Parse("6d4177ea-dac0-4d71-9246-40ae54465066");
    private static readonly Guid LayoutTypeKey = Guid.Parse("3356470c-9377-4d3c-8fdc-de84307539b2");
    private static readonly Guid HeadlineKey = Guid.Parse("aa202528-ddba-462d-984e-e25772671f67");
    private static readonly Guid LayoutKey = Guid.Parse("d07ead8b-1c0e-4a76-9209-e75f3ee6bab9");
    private static readonly Guid AreaKey = Guid.Parse("18137235-ef4b-4629-9b8b-4ee20b0db7ca");

    [Theory]
    [InlineData("")]
    [InlineData("/portal")]
    [ResetPublishedContentServices]
    public async Task Razor_previews_render_unsaved_variants_and_native_areas_without_replacing_AppBlocks(string prefix)
    {
        var root = Directory.CreateTempSubdirectory("block-preview-test-").FullName;
        var components = Directory.CreateDirectory(Path.Combine(root, "Views", "Partials", "blockgrid", "Components")).FullName;
        await System.IO.File.WriteAllTextAsync(Path.Combine(components, HeadlineAlias + ".cshtml"), """
            @using Umbraco.Extensions
            @inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage<Umbraco.Cms.Core.Models.Blocks.BlockGridItem>
            <h2 style="padding: 20px">@Model.Content.Value("headline")</h2>
            """, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(Path.Combine(components, LayoutAlias + ".cshtml"), """
            @using Umbraco.Extensions
            @inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage<Umbraco.Cms.Core.Models.Blocks.BlockGridItem>
            @using Umbraco.Community.BlockPreview.Extensions
            <div>@await Html.GetPreviewBlockGridItemAreasHtmlAsync(Model)</div>
            """, TestContext.Current.CancellationToken);
        try
        {
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            Root = root,
            EnvironmentName = "Production",
            VirtualPath = prefix,
            // The test publishes its own page; without the sample content it shares the install of the account tests.
            FirstBootGroup = "Settings",
            // Compiled models, as a deployed host can supply; no runtime generation required.
            StartSettings = { ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing" },
        }, TestContext.Current.CancellationToken);
        var api = prefix + "/umbraco/block-preview/api/v1";
        using var anonymous = site.CreateClient();
        using (var refused = await anonymous.GetAsync(api + "/settings", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using var admin = await site.SignInAsAdministratorAsync();

        var original = await admin.GetFromJsonAsync<BlockPreviewOptions>(api + "/settings", TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        // The starter export only contains AppBlocks elements. An empty eligible list must
        // not become the package client's wildcard and replace their authoring views.
        Assert.False(original.BlockGrid.Enabled);
        Assert.Contains("appForm", original.BlockGrid.IgnoredContentTypes);
        Assert.Contains("appReport", original.BlockGrid.IgnoredContentTypes);

        using var scope = site.CreateScope();
        var (page, pageType) = await InstallBlocksAsync(scope.ServiceProvider);
        var options = await admin.GetFromJsonAsync<BlockPreviewOptions>(api + "/settings", TestContext.Current.CancellationToken);
        Assert.NotNull(options);
        foreach (var editor in new[] { options.BlockGrid, options.BlockList, options.RichText, options.SingleBlock })
        {
            Assert.True(editor.Enabled);
            Assert.Contains(HeadlineAlias, editor.ContentTypes!);
            Assert.Contains(LayoutAlias, editor.ContentTypes!);
            Assert.DoesNotContain("appForm", editor.ContentTypes!);
            Assert.DoesNotContain("appReport", editor.ContentTypes!);
        }

        foreach (var (culture, expected, other) in new[] { ("en", "Unsaved English", "Texte français"), ("fr", "Texte français", "Unsaved English") })
        {
            using var response = await admin.PostAsJsonAsync(PreviewUrl(HeadlineAlias, HeadlineKey, culture),
                Blocks("Unsaved English", "Texte français"), TestContext.Current.CancellationToken);
            var markup = (await response.Content.ReadFromJsonAsync<string>(TestContext.Current.CancellationToken))!;
            Assert.True(response.IsSuccessStatusCode, markup);
            Assert.Contains(expected, WebUtility.HtmlDecode(markup));
            Assert.DoesNotContain(other, WebUtility.HtmlDecode(markup));
            Assert.DoesNotContain("Saved headline", markup);
        }

        using (var response = await admin.PostAsJsonAsync(PreviewUrl(LayoutAlias, LayoutKey, "en"),
            Blocks("Unsaved English", "Texte français"), TestContext.Current.CancellationToken))
        {
            var markup = (await response.Content.ReadFromJsonAsync<string>(TestContext.Current.CancellationToken))!;
            Assert.True(response.IsSuccessStatusCode, markup);
            Assert.Contains("<umb-block-grid-areas-container slot=\"areas\">", markup);
        }
        Assert.Contains("Saved headline", scope.ServiceProvider.GetRequiredService<IContentService>().GetById(page.Key)!.GetValue<string>("body", "en"));

        var styles = await admin.GetFromJsonAsync<string[]>(api + $"/preview/grid/stylesheets?nodeKey={page.Key}&documentTypeUnique={pageType.Key}", TestContext.Current.CancellationToken);
        Assert.Contains(prefix + "/dist/css/site.css", styles!);
        foreach (var asset in new[] { prefix + "/dist/css/site.css", prefix + "/App_Plugins/Umbraco.Community.BlockPreview/index.js" })
        {
            using var response = await anonymous.GetAsync(asset, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        string PreviewUrl(string alias, Guid key, string culture) => api +
            $"/preview/grid?nodeKey={page.Key}&documentTypeUnique={pageType.Key}&blockEditorAlias=body&contentElementAlias={alias}&contentUdi={key}&culture={culture}";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(IContent, IContentType)> InstallBlocksAsync(IServiceProvider services)
    {
        var types = services.GetRequiredService<IContentTypeService>();
        var dataTypes = services.GetRequiredService<IDataTypeService>();
        var strings = services.GetRequiredService<IShortStringHelper>();
        var text = (await dataTypes.GetByEditorAliasAsync("Umbraco.TextBox")).First();
        var headline = new ContentType(strings, -1)
        {
            Key = HeadlineTypeKey, Alias = HeadlineAlias, Name = "Preview headline", IsElement = true, Variations = ContentVariation.Culture,
        };
        headline.AddPropertyType(new PropertyType(strings, text)
        {
            Alias = "headline", Name = "Headline", Variations = ContentVariation.Culture,
        });
        Assert.True((await types.CreateAsync(headline, Constants.Security.SuperUserKey)).Success);
        var layout = new ContentType(strings, -1) { Key = LayoutTypeKey, Alias = LayoutAlias, Name = "Preview layout", IsElement = true };
        Assert.True((await types.CreateAsync(layout, Constants.Security.SuperUserKey)).Success);

        var grid = new DataType(services.GetRequiredService<DataEditorCollection>().First(editor => editor.Alias == "Umbraco.BlockGrid"),
            services.GetRequiredService<IConfigurationEditorJsonSerializer>())
        {
            Name = "Preview test grid", EditorUiAlias = "Umb.PropertyEditorUi.BlockGrid",
            ConfigurationData = new Dictionary<string, object>
            {
                ["gridColumns"] = 12,
                ["blocks"] = new object[]
                {
                    new { contentElementTypeKey = HeadlineTypeKey, allowAtRoot = true, allowInAreas = true },
                    new { contentElementTypeKey = LayoutTypeKey, allowAtRoot = true, allowInAreas = true,
                        areas = new[] { new { key = AreaKey, alias = "main", columnSpan = 12 } } },
                },
            },
        };
        Assert.True((await dataTypes.CreateAsync(grid, Constants.Security.SuperUserKey)).Success);
        var pageType = new ContentType(strings, -1)
        {
            Alias = "blockPreviewTestPage", Name = "Block preview test page", AllowedAsRoot = true, Variations = ContentVariation.Culture,
        };
        pageType.AddPropertyType(new PropertyType(strings, grid) { Alias = "body", Name = "Body", Variations = ContentVariation.Culture });
        Assert.True((await types.CreateAsync(pageType, Constants.Security.SuperUserKey)).Success);
        var contents = services.GetRequiredService<IContentService>();
#pragma warning disable CS0618
        var page = contents.Create("Preview page", -1, pageType.Alias);
        foreach (var culture in new[] { "en", "fr" })
        {
            page.SetCultureName("Preview page", culture);
            page.SetValue("body", Blocks("Saved headline", "Titre enregistré"), culture);
        }
        Assert.True(contents.Save(page).Success);
        Assert.True(contents.Publish(page, ["*"]).Success);
#pragma warning restore CS0618
        return (page, pageType);
    }

    private static string Blocks(string english, string french) => new JsonObject
    {
        ["layout"] = new JsonObject { ["Umbraco.BlockGrid"] = new JsonArray(new JsonObject
        {
            ["contentKey"] = LayoutKey, ["columnSpan"] = 12, ["rowSpan"] = 1,
            ["areas"] = new JsonArray(new JsonObject { ["key"] = AreaKey, ["items"] = new JsonArray(new JsonObject
            {
                ["contentKey"] = HeadlineKey, ["columnSpan"] = 12, ["rowSpan"] = 1, ["areas"] = new JsonArray(),
            }) }),
        }) },
        ["contentData"] = new JsonArray(
            new JsonObject { ["key"] = LayoutKey, ["contentTypeKey"] = LayoutTypeKey, ["values"] = new JsonArray() },
            new JsonObject { ["key"] = HeadlineKey, ["contentTypeKey"] = HeadlineTypeKey, ["values"] = new JsonArray(
                new JsonObject { ["alias"] = "headline", ["value"] = english, ["culture"] = "en", ["segment"] = null, ["editorAlias"] = "Umbraco.TextBox" },
                new JsonObject { ["alias"] = "headline", ["value"] = french, ["culture"] = "fr", ["segment"] = null, ["editorAlias"] = "Umbraco.TextBox" }) }),
        ["settingsData"] = new JsonArray(),
        ["expose"] = new JsonArray(new[] { LayoutKey, HeadlineKey }.SelectMany(key => new[] { "en", "fr" }.Select(culture =>
            (JsonNode)new JsonObject { ["contentKey"] = key, ["culture"] = culture, ["segment"] = null })).ToArray()),
    }.ToJsonString();

    [PublishedModel(HeadlineAlias)]
    public sealed class PreviewHeadline(IPublishedElement content, IPublishedValueFallback fallback) : PublishedElementModel(content, fallback);

    [PublishedModel(LayoutAlias)]
    public sealed class PreviewLayout(IPublishedElement content, IPublishedValueFallback fallback) : PublishedElementModel(content, fallback);
}
