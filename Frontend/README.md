# Frontend

This folder is the source of the library's two themes: the `website` theme and the
`application` theme. One build produces the stylesheets and scripts that every site
built on the library uses. A site's branded theme is a library of its own with its own
frontend, as [Themes](../docs/themes.md#a-theme-library) describes.

## Build

Use Node 20 or newer. From this folder:

```powershell
npm ci
npm run build
npm run check
npm test
```

The .NET build of `src/UmbracoAppHost` runs `npm ci` and `npm run build` itself when
the sources change and copies `dist/` into the library's `wwwroot/dist/`. Run
`npm run check` and `npm test` here before publishing a site.

| Command | Purpose |
|---|---|
| `npm run build` | Production assets with external source maps in `dist/`. |
| `npm run build:dev` | Readable assets with inline source maps. |
| `npm run watch` | Rebuilds assets when their source changes. |
| `npm start` | Builds, watches and serves `Frontend/` at `http://127.0.0.1:8000/`. Stop it with Ctrl+C so it releases the port. |
| `npm run check` | Checks JavaScript types and JSDoc contracts. |
| `npm test` | Runs the form runtime and website behavior tests. Build the assets first. |
| `npm run test:blocks` | Runs the form runtime tests alone. |
| `npm run test:previews` | Runs the [block preview](../docs/block-previews.md#tests) tests. |
| `npm run clean` | Removes the generated assets from `dist/`. |

## Sources and outputs

| Source | Output |
|---|---|
| `src/website/` | `dist/scripts/site.js` and `dist/css/site.css`, the website theme. |
| `src/app/` | `dist/scripts/app.js` and `dist/css/app.css`, the application theme. |
| `src/signin.js`, `src/scss/components/signin.scss` | `dist/scripts/signin.js` and `dist/css/signin.css`, the member sign-in form. |
| `src/scss/` | The block styles both themes share: `site/_blockgridlayout.scss`, Umbraco's grid, and `components/_forms.scss`, the forms. A theme of your own can import both. |
| `test/` | The tests. |

Neither bundle needs jQuery or Bootstrap. Razor layouts, partials, page views and C#
code are authored directly in `src/UmbracoAppHost/Views/`.

## Capture a rendered page

Capture a page from a running site when a change depends on markup an editor wrote:

```powershell
npm run capture -- --origin=https://localhost:44400
npm run capture -- /en/home --name=home --origin=https://localhost:44393
```

The tool writes fixtures under `demo/templates/`, which Git ignores. It replaces the site's
bundles with the local ones and points other root-relative addresses at the site. Capture again
when the site's markup changes.
