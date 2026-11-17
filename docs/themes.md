# Themes

A theme gives a Web Site its page layout, its sign-in layout and the styling of the blocks.
The library ships two, and a site adds its own by registering it. This page describes how a
theme is chosen, what one consists of, and how to package one as a library.

## The two themes

| Alias | Page layout | Sign-in layout | Print stylesheet | Look |
|---|---|---|---|---|
| `application` | `AppMaster.cshtml` | `AppSignIn.cshtml` | `/App_Plugins/PaperReports/application-print.css` | An application shell: a header with the account menu, a side menu, and regions with title bars. |
| `website` | `WebsiteLayout.cshtml` | `WebsiteSignIn.cshtml` | `/App_Plugins/PaperReports/website-print.css` | A small public site: a top bar with the menu, a sidebar and plain typography. |

`AppMaster.cshtml`, `AppSignIn.cshtml` and `WebsiteLayout.cshtml` are generated from the
templates in [`Frontend`](../Frontend/README.md). The build writes them into the library's
`Views/` folder and embeds them. Both themes' assets are in one build: `site.css` and `site.js`
for the website theme, `app.css` and `app.js` for the application theme.

## How a page gets its theme

A page takes the theme of its nearest Web Site. `SiteThemeCatalog` resolves it in this order:

1. The site's **Theme** property (`theme`, on the Appearance tab), when it names a registered
   theme.
2. The default of the site's template: **Website home** selects `website`, and **Application
   site** selects `application`.
3. `application` when the site's template is Application site, and `website` otherwise. This
   step applies to views that pass no default, such as Report Page and Site sign in.

A signed-in visitor gets the theme's page layout. Views that require sign-in, such as Report
Page, give an anonymous visitor the sign-in layout.

## Register a theme

Register a `SiteTheme` with its alias and the file names of its two layouts, and an optional
print stylesheet for paper reports:

```csharp
builder.Services.AddSingleton(new UmbracoAppHost.Themes.SiteTheme(
    "custom", "CustomMaster.cshtml", "CustomSignIn.cshtml", "/css/custom-print.css"));
```

Enter the alias in a Web Site's **Theme** property to use it. A theme registered under the alias
`website` or `application` replaces that built-in theme for every site that uses it, with no
change to stored content.

The layouts supply the theme's stylesheet and render the body. Give them file names of their
own: the library rewrites the views it ships at every start, so a layout named like one of
them would be replaced.

## What a theme styles

AppBlocks' block partials emit the same markup under every theme: Umbraco's block grid classes
and custom properties, which Umbraco's `umbraco-blockgridlayout.css` turns into columns, and the
hooks below. Each theme styles the hooks.

| Hook | Element |
|---|---|
| `ah-grid`, `ah-grid__item` | A grid of blocks: the page, and the inside of every area. `data-grid-columns` gives its width. A field with a width adds `ah-grid__item--width-N`. |
| `ah-layout`, with `ah-layout--full-width`, `--two-columns` or `--three-columns`, and `ah-layout--split-4-8`, `-6-6` or `-8-4` | A column layout. |
| `ah-area`, `ah-area--<alias>` | An area of a column layout. `data-area-col-span` gives its span. |
| `ah-region`, `ah-region--collapsible`, `ah-region--untitled`, `ah-region__header`, `ah-region__title`, `ah-region__body` | A region. A collapsible one is a `details` element whose `summary` is the title bar, so it folds without script. |
| `ah-rich-text` | The output of a Rich text block. |

The two themes style them in these files:

| Theme | Stylesheet | Columns stack |
|---|---|---|
| Application | `Frontend/src/app/scss/_layouts.scss` | At 48rem and below, where the side menu becomes an overlay. |
| Website | `Frontend/src/website/scss/_blocks.scss` | Below 48rem. |

Twelve grid tracks have eleven gaps whatever the blocks span, so both themes cap the column gap
at 3% of the grid's width. A fieldset in a narrow column then stays inside it.

A theme of your own can import the two files every theme shares:
`Frontend/src/scss/site/_blockgridlayout.scss`, the grid, and
`Frontend/src/scss/components/_forms.scss`, the forms. The forms file reads the tokens
`--ah-accent`, `--ah-radius`, `--ah-surface`, `--ah-surface-alt`, `--ah-border`, `--ah-ink`,
`--ah-muted`, `--ah-input-border` and `--ah-form-padding`, which the theme defines. AppBlocks'
[styling guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/site-integration.md#styling)
lists the `--app-` tokens of the blocks' own stylesheet.

## A theme library

A branded theme is a Razor class library with its own frontend build. A site references it and
gets the theme. The library registers three things from an `IComposer`:

```csharp
public sealed class MyThemeComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton(new SiteTheme("website", "MyLayout.cshtml", "MySignIn.cshtml"));
        builder.Services.AddSingleton(new ThemeLibrary(typeof(MyThemeComposer).Assembly));
        builder.Services.AddLoginScreen(builder.Config, "mytheme/logo.svg", "mytheme/panel.jpg");
    }
}
```

- `SiteTheme` names the layouts, as above.
- `ThemeLibrary` hands the host the assembly's embedded files. Its `Views/` folder is supplied
  as the library's own views are: Razor finds every view, and the top-level ones are written to
  the project's `Views/` folder before uSync imports. Its `uSync/` files are copied into a new
  working uSync folder, at paths that neither the library's export nor the project's export
  ships. The project file sets `GenerateEmbeddedFilesManifest` to `true` and embeds both folders.
- `AddLoginScreen` is optional and brands the login screen; see
  [Login screen and backoffice](backoffice.md#brand-the-login-screen).

Serve the library's static files under paths of its own, so they do not replace the library's.
