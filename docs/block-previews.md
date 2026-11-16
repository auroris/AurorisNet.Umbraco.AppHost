# Block editor preview coverage

UmbracoAppHost includes `Umbraco.Community.BlockPreview` 6.3.0 for Razor previews in Block Grid,
Block List, Rich Text and Single Block editors. The package supports Umbraco 18 and discovers
the host's conventional block partials and ViewComponents. Its preview endpoints require a
backoffice login.

AppBlocks and its companions keep their existing custom authoring views and native layouts.
`Hosting/BlockPreviews.cs` adds the element aliases from their registered schemas to the package's
ignored types. These previews keep forms, reports and other data-driven controls inert. When all
elements are excluded, the host disables the package's empty selection in its settings response;
otherwise BlockPreview 6.3 would interpret the empty list as a wildcard.

Both implementations use Umbraco's `blockEditorCustomView` extension. Umbraco owns the action bar,
sorting, resizing, area creation and validation.

## Razor preview setup

The host enables all four editor types and loads `/dist/css/site.css` by default. Stylesheet URLs
include `Umbraco:CMS:Hosting:ApplicationVirtualPath`, so `/wing` sites load their own assets.
Use the package's standard `BlockPreview` settings in the consuming site's configuration to
choose content types, view locations, stylesheets or enabled editors. For example:

```json
{
  "BlockPreview": {
    "BlockGrid": {
      "ContentTypes": ["heroBlock", "richTextBlock"],
      "Stylesheets": ["/css/my-theme.css"]
    },
    "RichText": { "Enabled": false }
  }
}
```

An explicit `ContentTypes` selection overrides the package's ignored list. Keep AppBlocks aliases
out of that selection unless the site supplies its own compatible preview views. A downstream
plugin with another custom preview can add its aliases to `IgnoredContentTypes`.

BlockPreview requires generated models marked with `PublishedModel`. A host can use Umbraco's
`InMemoryAuto` mode with the existing DevelopmentMode.Backoffice package, or compile generated
models into its assembly and deploy with `ModelsMode=Nothing`. A site that selects Umbraco's
`Production` runtime mode must also precompile its Razor views, as it does for ordinary page
rendering. AppHost's embedded views use its existing runtime-compilation setup.

The shared grid partials use the package's preview helpers. A custom layout partial should call
`GetPreviewBlockGridItemAreasHtmlAsync` from `Umbraco.Community.BlockPreview.Extensions` so its
children remain native editable areas. Ordinary page requests keep the normal grid rendering.
The host's headline, rich text, image and two-column demo partials use BlockPreview; their old
JavaScript preview registrations have been removed.

See the package's [configuration](https://github.com/rickbutterfield/BlockPreview/blob/v6/dev/docs/configuration.md)
and [view integration](https://github.com/rickbutterfield/BlockPreview/blob/v6/dev/docs/usage.md) guides.

## Existing custom preview coverage

Checked against the installed 4Wing development database on 2026-11-04: all **41 distinct
element types offered by its five block-editor data types** have a content preview or native layout view.
The source libraries own one additional portable block (`appFormsContainer`), also covered.
That check additionally covered 19 retained legacy 4Wing aliases and four host demo aliases.
The host demo aliases now use BlockPreview. AppBlocks and the legacy 4Wing pure layouts keep
their native area representation. Settings-only elements are displayed through their parent.

| Owner | Element aliases | Preview |
| --- | --- | --- |
| AppBlocks | `appFullWidth`, `appThreeColumns` | Unmodified native block view and editable areas. |
| AppBlocks | `appColumns` | Native editable areas inside a custom view that reflects split settings `4/8`, `6/6`, `8/4`. |
| AppBlocks | `appRegion`, `appFieldset` | Authored heading/help, collapsible state, native editable area. |
| AppBlocks | `appRichText`, `appHtml` | Current unsaved markup in an isolated document. |
| AppBlocks | `appCss`, `appScript` | Authored source; page scripts do not run. |
| AppBlocks | `appForm`, `appFormInterface`, `appFormsContainer` | Heading, form class/mode, native form areas; nested container contents previewed in layout order. |
| AppBlocks | `appText`, `appTextArea`, `appNumber`, `appDate`, `appTime`, `appDateTime`, `appCheckbox`, `appSelect`, `appShuttle`, `appColor`, `appDisplay` | Disabled controls with authored labels, defaults, placeholders, required/help, hidden and read-only indicators. |
| AppBlocks | `appActions` | Authored action labels on disabled buttons. |
| AppBlocks | `appAttachments` | File selection/drop area and attachment table outline, heading/help and allowed types. |
| AppBlocks | `appTabular` | Configured column names, empty message and action labels. |
| AppBlocks | `appSchedule` | Timeline example, heading/help, window and source names. |
| AppBlocks | `appAntiSpam` | Verification placeholder with service and size; no challenge or network verification. |
| AppBlocks.InteractiveReports | `appReport` | Report heading, table, search/actions, data source and page size. |
| 4WingResources | `fourWingImage`, `fourWingCarouselItem` | Selected image, alternative text and caption. |
| 4WingResources | `fourWingCarousel` | All authored slides, captions and presentation settings. |
| 4WingResources | `fourWingTabs`, `fourWingAccordion`, `fourWingTitledText` | All authored titles and rich text, including nested list ordering and culture. Panels remain visible for authoring. |
| 4WingResources | `fourWingChildSites`, `fourWingPageNavigation`, `fourWingLogoGallery` | Clearly labelled page-context examples; destinations/badges are resolved by the public page. |
| 4WingResources | `fourWingWbgt` | Reading layout with empty values; the live reading is fetched by the public page. |
| 4WingDiagrams | `fourWingDiagram` | Selected diagram image and caption; viewer/editor scripts do not run. |
| 4WingTelephoneDirectory | `fourWingTelephoneDirectory` | Search/table outline and actual view, scope and filter settings. |
| 4WingFileRepository | `fourWingFileRepository` | Search/table outline; no upload/download or data-changing action. |

Legacy 4Wing aliases retained alongside the old Razor views: `blockCSS`,
`blockDeleteManagementCookies`, `blockDiagram`, `blockDropDownList`, `blockHtml`, `blockImage`,
`blockImageCarousel`, `blockJavascript`, `blockListBadgedWebsites`, `blockPageNextPrevious`,
`blockRichText`, `blockTabbedList`, `blockTwoColumn1`, `blockTwoColumn2`, `blockTwoColumn3`,
`blockUnitLogoGallery`, `blockVideoPlayer`, `blockRichTextListItem`, `blockImageCarouselItem`.
These receive the corresponding content, layout or disabled authoring representation.
The three `blockTwoColumn*` aliases keep their native view and configured areas.
They are not currently offered by the installed site's data types.

Host demo aliases: `umbBlockGridDemoHeadlineBlock`, `umbBlockGridDemoRichTextBlock`,
`umbBlockGridDemoImageBlock`, `umbBlockGridDemoTwoColumnLayoutBlock`.
The demo two-column layout renders its Razor wrapper with native editable areas.

`appColumnsSettings`, `appRegionSettings` and `fourWingMediaPresentation` are settings models,
not selectable content blocks. AppBlocks.Documents supplies a paper-report document type,
not an additional block. ActivityScheduler and AerodromeStatus supply data sources consumed
by the existing schedule/report/form blocks; neither owns another editor block.

## Implementation and limits

Shared code and core registrations live in AppBlocks' `wwwroot/App_Plugins/ApplicationBlocks`.
Each companion/plugin supplies its own manifests and small renderer. There is no wildcard
custom view that could silently take over an unrelated site's blocks.

Umbraco's installed default block view is a labelled card with native areas. Native inline
editing is a property-editing mode. BlockPreview supplies the Razor renderer described above.
References:
[v18 manifest API](https://apidocs.umbraco.com/v18/ui-api/interfaces/packages_block_block-custom-view.ManifestBlockEditorCustomView.html),
[official custom-view documentation](https://docs.umbraco.com/umbraco-cms/extend-your-project/backoffice-extensions/extending-overview/extension-types/block-custom-view),
[BlockPreview supported versions](https://github.com/rickbutterfield/BlockPreview#supported-versions).

The AppBlocks custom views use native editor values. Their frontend Razor pipeline resolves
form classes, visitor identity, record queries and page context, so these blocks retain their
existing authoring renderer. Markup and media are presented directly, using Umbraco's media
repository and existing AppBlocks/host/4Wing stylesheets. The small 4Wing presentation
markup mirrors the relevant Razor hooks; changes to those hooks should update its preview.
This is an authoring preview, not a pixel-identical full-page rendering.

In these custom views, HTML is sanitized with Umbraco's bundled DOMPurify and placed in a sandboxed, inert iframe
without script or form permissions. Its CSP disallows scripts, connections, nested frames
and form submission. Only same-origin/data images and same-origin styles/fonts load.
Authored inline styles are isolated inside the preview. Content referring to external
images, embeds or scripts needs the normal page preview. Standalone CSS/JavaScript blocks
are shown as source and are not applied to other block previews.

Fields show explicitly authored values; resource-derived labels/defaults and live list
choices are not queried. Report, schedule, attachment, repository, directory and WBGT
previews do not fetch business records. Hidden fields are deliberately visible and labelled
in the editor. Form/container nested property editors open through the normal edit link;
their inline preview is read-only, while actual grid areas retain native editing.

## Verification

`BlockPreviewTests` starts isolated hosts at `/` and `/wing`. It checks backoffice authentication,
AppBlocks exclusions (including an empty eligible selection), unsaved English/French Razor
previews, native nested areas, stylesheet paths and package assets. The fixture supplies compiled
models and disables runtime model generation, matching a deployment with compiled models.

A browser check on an isolated `/wing` host also confirmed nested Razor rendering, editing a
headline without saving the page, and preservation of AppBlocks' disabled form controls.

From `UmbracoAppHost/Frontend`:

```powershell
$env:PREVIEW_TEST_ORIGIN = 'http://localhost:5094'
npm run test:previews
node --test test/templates.mjs test/website.mjs
```

The coverage test scans owning schemas, all configured block references and extension
manifests. Rendering tests cover controls, escaping, nested ordering and culture. The
component test bundles the modules served by the running host's actual import map and
renders them in JSDOM with real Umbraco/Lit/DOMPurify code. Without `PREVIEW_TEST_ORIGIN`,
that integration test explicitly skips. JSDOM does not verify visual layout or pointer drag.

The earlier 4Wing coverage check included its build and HTTP checks for preview assets.
That check did not include browser interaction. For a consuming site's visual QA, check the
following in its actual backoffice:

- Open the page containing Two columns, Rich text, HTML and Tabbed content. Verify content
  is visible, tabs show their bodies and each preview opens its own content editor.
- Edit content and settings without publishing; verify updates, split widths and culture.
- Add/reorder a child within a native area, move it across areas, resize a permitted block,
  and inspect required-field validation and read-only access.
- Open a form with fields, report, schedule, image/carousel and plugin block. Verify controls
  are inert, media resolves and native editing still works.
