# App Host

Reusable Umbraco 18 foundation bundling [AppBlocks](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks), single sign-on, themes, virtual-path hosting, starter uSync definitions, and site-management scripts.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-18.svg)
![Status: alpha](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/status-alpha.svg)

## What's included

- **Page types & themes:** Web Site and Web Page types with website/application themes, authored menus, language fallback, and block previews (`Umbraco.Community.BlockPreview`, `Umbraco.Community.UmbNav`).
- **AppBlocks bundle:** Forms, SQL data sources, Interactive Reports, Documents, and a directory-backed [People picker](docs/people-picker.md).
- **Auth & accounts:** Embedded/external Windows SSO (`IdentityServer.SSO`), **Add from directory** (`IdentityServer.Directory`), and break-glass admin recovery (`BreakGlass`).
- **Hosting defaults:** External instance folder (`instance.json`, DB, media, uSync), virtual-path hosting (`ApplicationVirtualPathFix`), disabled telemetry (`DisableTelemetry`), and a Welcome dashboard.
- **Testing package:** `AurorisNet.Umbraco.AppHost.Testing` boots a host in-process on Kestrel with a fresh SQLite database.

## Quick start

Run the reference website at `http://localhost:5080`. On a fresh install, sign in at `/umbraco` with
`UnattendedUserEmail` and `UnattendedUserPassword` from `reference/instance.json`.

```powershell
dotnet run --project src/UmbracoAppHost.Web
```

Or use the library in your own web project (`Program.cs`):

```csharp
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.RunAsync();
```

Use `AddUmbracoAppHost<Program>()` / `UseUmbracoAppHostAsync<Program>()` when implementing `IUmbracoAppHost` so tests apply the same registrations. Create or update standalone site folders with `new-site.ps1`, `update-site.ps1`, `refresh-definitions.ps1`, and `reset.ps1` (see [Deployment](docs/deployment.md)).

## Settings

| Setting | Default | Effect |
|---|---|---|
| `InstanceRoot` | `App_Data/UmbracoAppHost` | Instance folder holding `instance.json`, `data/`, `media/`, and `uSync/`. |
| `UmbracoAppHost:HostExport` | `uSync` | Project's own uSync export folder copied over the library's export (empty disables). |
| `UmbracoAppHost:ProjectRoot` | content root | Folder holding project files such as uSync exports and schema scripts. |
| `UmbracoAppHost:ImportDefinitions` | `true` | Imports shared uSync definitions on first boot of each build. |

Settings layer in order: library `defaults.json` → `instance.json` → `appsettings*.json` → environment variables → CLI args.

## Documentation

| Guide | Covers |
|---|---|
| [Getting started](docs/getting-started.md) | Reference site, using the library in another repo, testing a host |
| [Deployment](docs/deployment.md) & [uSync exports](docs/usync-exports.md) | `new-site.ps1` / `update-site.ps1`, IIS, shared definitions |
| [Websites, pages and blocks](docs/application-blocks.md) & [Themes](docs/themes.md) | Page types, menus, themes, layouts, sample pages |
| [Sign-in and accounts](docs/sign-in-and-accounts.md) & [People picker](docs/people-picker.md) | SSO, directory accounts, break glass, form people picker |
| [Interactive Reports](docs/interactive-reports.md) & [Databases](docs/databases.md) | Reports, SQLite/SQL Server, app databases |
| [Hosting under a sub-path](docs/hosting-under-a-sub-path.md), [Backoffice](docs/backoffice.md), [Block previews](docs/block-previews.md) | Virtual paths, login branding, Razor previews |
| [Design](docs/design-blocks-and-data-sources.md) & [Migrations](Migrations/README.md) | Architecture boundaries and release upgrade snapshots |
| [Developing](docs/developing.md) & [Frontend](Frontend/README.md) | Sibling checkouts, build/test commands, pinned versions, releases |

## License

[MIT](LICENSE)
