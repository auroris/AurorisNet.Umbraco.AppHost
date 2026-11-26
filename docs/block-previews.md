# Block previews

Editors see a rendered preview of each block in the Block Grid, Block List, Rich Text and Single
Block editors. Two mechanisms supply them, and this page describes how they share the editors
and how a site configures the Razor one.

| Blocks | Preview from |
|---|---|
| A site's own blocks with conventional Razor partials or view components | [`Umbraco.Community.BlockPreview`](https://github.com/rickbutterfield/BlockPreview), which renders the block's Razor view with its unsaved values. |
| AppBlocks' blocks: layouts, fields, forms, reports and the others | AppBlocks' own authoring views, which draw the block without running any form class, SQL or script. AppBlocks' [preview guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/site-integration.md#previews-in-the-backoffice) describes them. |

Both use Umbraco's `blockEditorCustomView` extension, so Umbraco keeps the action bar, sorting,
resizing, area creation and validation. The package's preview endpoints need a backoffice login.

## What the host sets up

- **AppBlocks' blocks are excluded from Razor previews.** `Hosting/BlockPreviews.cs` adds the
  alias of every element type in the registered AppBlocks schemas to the package's
  `IgnoredContentTypes`, for all four editors.
- **All four editors are on, with the website theme's stylesheet.** The library's defaults set
  `Enabled` to `true` and `Stylesheets` to `/dist/css/site.css` for `BlockGrid`, `BlockList`,
  `RichText` and `SingleBlock`.
- **Stylesheets follow the virtual path.** A root-relative stylesheet gets the site's
  `Umbraco:CMS:Hosting:ApplicationVirtualPath` in front, so a site at `/portal` loads
  `/portal/dist/css/site.css`.
- **An empty selection stays empty.** When every element is excluded, the package's settings
  answer carries an empty `ContentTypes` list, which its client would read as "all types". The
  host turns that editor's previews off in the answer.
- **The shared grid partials render previews.** The library's `Views/Partials/blockgrid/`
  partials call the package's preview helpers, so nested areas stay editable. Ordinary page
  requests render the grid as before.

## Settings

Use the package's `BlockPreview` section in the site's settings to choose content types, view
locations, stylesheets or editors:

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

An explicit `ContentTypes` list takes precedence over the ignored list. Keep AppBlocks' aliases
out of it unless the site supplies preview views for them. A plugin with a preview of its own
adds its aliases to `IgnoredContentTypes`.

See the package's [configuration](https://github.com/rickbutterfield/BlockPreview/blob/v6/dev/docs/configuration.md)
and [view integration](https://github.com/rickbutterfield/BlockPreview/blob/v6/dev/docs/usage.md)
guides.

## Requirements for a site's own blocks

- The package needs generated models marked with `PublishedModel`. Use Umbraco's `InMemoryAuto`
  models mode, which the library's `Umbraco.Cms.DevelopmentMode.Backoffice` reference supports,
  or compile the generated models into the site's assembly and run with `ModelsMode` set to
  `Nothing`.
- A site that selects Umbraco's `Production` runtime mode must precompile its Razor views, as it
  must for page rendering.
- A layout partial with areas calls `GetPreviewBlockGridItemAreasHtmlAsync` from
  `Umbraco.Community.BlockPreview.Extensions`, so its children remain editable areas.

## Limits

A preview shows authoring content. It does not reproduce the page: the block is drawn with the
preview stylesheets alone, outside the page's layout. AppBlocks' previews fetch no business
data, so a field shows its authored label and default, and a report, schedule or attachments
block shows representative markup.

## Tests

`BlockPreviewTests` starts hosts at `/` and under `/portal`. It checks that the preview endpoints
need a backoffice login, that AppBlocks' aliases are excluded, and that Razor previews render
unsaved English and French values with their nested areas and prefixed stylesheets.

The frontend has two preview tests. The first scans the schemas and block editor configurations
of the sibling checkouts and fails when an element type has no preview. The second renders the
preview modules a running host serves in JSDOM, and is skipped when `PREVIEW_TEST_ORIGIN` is not
set:

```powershell
cd Frontend
$env:PREVIEW_TEST_ORIGIN = 'http://localhost:5080'
npm run test:previews
```

JSDOM checks neither visual layout nor dragging. Check those in a real backoffice: edit a block
without publishing, move a child between areas, and confirm that form controls in a preview
stay disabled.
