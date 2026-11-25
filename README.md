# Umbraco App Host

`AurorisNet.Umbraco.AppHost` is a reusable Umbraco 18 foundation for websites and business
applications: one library that gives a web project sign-in, page types, themes, forms, reports
and the scripts that create and update its sites.

![MIT license](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/license-mit.svg)
![.NET 10](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/dotnet-10.svg)
![Umbraco 18](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/umbraco-18.svg)
![Status: alpha](https://raw.githubusercontent.com/auroris/AurorisNet.Build/main/badges/status-alpha.svg)

A web project references the library and makes two calls in `Program.cs`. The library registers
Umbraco with its backoffice and website, the packages listed under
[What a site gets](#what-a-site-gets), the shared views and browser assets, and a saved uSync
export that the first start imports into a new database.

The repository holds:

| Path | Contents |
|---|---|
| `src/UmbracoAppHost` | The library, packed as `AurorisNet.Umbraco.AppHost`. |
| `src/UmbracoAppHost.Web` | The reference website: a `Program.cs` with the two calls, and the project the site scripts publish. |
| `tests/UmbracoAppHost.Testing` | `AurorisNet.Umbraco.AppHost.Testing`, which boots a host in a test process. |
| `Frontend` | The source of the website and application themes, built into the library. |
| `new-site.ps1`, `update-site.ps1`, `refresh-definitions.ps1`, `reset.ps1`, `deployment/` | The scripts that create, update and reset sites. |

A site can reference the `AurorisNet.Umbraco.AppHost` package or build the library from this checkout. Only the package ids carry the `AurorisNet`
prefix: the assembly, the namespace and the settings section are `UmbracoAppHost`.

Installation snapshots, upgrade steps and release checks are described in [Release migrations](Migrations/README.md).

## Start a site

Run `git submodule update --init` in the AppHost checkout to get the pinned AppBlocks source. A new clone can use `git clone --recurse-submodules`. Package consumers only need the AppHost package.

Building from source needs the .NET 10 SDK, Node 20 or newer, and these checkouts beside this repository's
folder. The scripts need PowerShell 7.

| Sibling checkout | Provides | Folder property | Source switch |
|---|---|---|---|
| `../AurorisNet.Umbraco.BreakGlass` | Administrator recovery | `BreakGlassRoot` | `UseBreakGlassSource` |
| `../AurorisNet.Umbraco.IdentityServer.SSO` | Single sign-on | `IdentityServerSsoRoot` | `UseIdentityServerSsoSource` |
| `../AurorisNet.Umbraco.IdentityServer.Directory` | Add from directory | `IdentityServerDirectoryRoot` | `UseIdentityServerDirectorySource` |

Pass a folder property to use a checkout elsewhere, for example
`dotnet build -p:BreakGlassRoot=D:\src\AurorisNet.Umbraco.BreakGlass`. Each switch is `true` by
default; [Developing](#developing) describes what `false` does.

**Run the reference website.** Open `UmbracoAppHost.slnx` in Visual Studio and start the
**Website** launch profile, or run:

```powershell
dotnet run --project src/UmbracoAppHost.Web
```

The first start creates the instance folder `reference/`, installs Umbraco into a SQLite
database and imports the saved export with its sample pages. The site answers at
`http://localhost:5080/` and the backoffice at `/umbraco`. Under the Development settings, sign
in as `admin` with any nonempty password. [Getting started](docs/getting-started.md) explains
the sign-in and how to reset the instance.

**Create independent sites.** `new-site.ps1` publishes the reference website and creates a site
folder from the build. `update-site.ps1` installs a newer build into it.
[Deployment](docs/deployment.md) covers both, and IIS.

```powershell
pwsh -File new-site.ps1 -Name MyWebsite -Destination E:/Sites/MyWebsite
pwsh E:/Sites/MyWebsite/start-site.ps1
```

**Use the library in your own project.** Reference `src/UmbracoAppHost/UmbracoAppHost.csproj`
from a web project and call the library from `Program.cs`:

```csharp
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync();
await app.RunAsync();
```

`AddUmbracoAppHost` reads the instance settings and registers Umbraco and the shared services.
Its optional callback registers the project's own database connections.
`UseUmbracoAppHostAsync` starts Umbraco and builds the request pipeline. Its two optional
callbacks add middleware and endpoints inside Umbraco's pipeline. A project that keeps its
registrations on a class implementing `IUmbracoAppHost` calls `AddUmbracoAppHost<Program>()` and
`UseUmbracoAppHostAsync<Program>()`, so its tests apply the same registrations.
[Use the library in another repository](docs/getting-started.md#use-the-library-in-another-repository)
has the project file, settings and update routine.

| Script | Purpose |
|---|---|
| `new-site.ps1 -Name <name> -Destination <folder>` | Publishes the reference website, or the project `-Project` names, and creates an independent site from the build. |
| `update-site.ps1 -Site <folder>` | Installs a new build into a site and refreshes the shared uSync definitions in its `uSync` folder. The site imports them at its next start. |
| `refresh-definitions.ps1 -Instance <folder>` | Refreshes the shared uSync definitions of an instance that a host runs from its own project, for a host that keeps running. |
| `reset.ps1` | Deletes the reference instance's data, so the next start installs a new database and imports the saved export. |

## What a site gets

- **Web Site and Web Page types** with a website theme and an application theme, authored menus,
  page CSS and JavaScript, and language fallback. See
  [Websites, pages and blocks](docs/application-blocks.md) and [Themes](docs/themes.md).
- **Forms, data sources, reports and documents** from
  [AppBlocks](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks) and its Interactive
  Reports and Documents companions, with sample pages over a sample orders database. See
  [Websites, pages and blocks](docs/application-blocks.md) and [Interactive Reports](docs/interactive-reports.md).
- **Single sign-on** for backoffice users and members through an embedded identity server that
  signs in Windows accounts, with a password fallback, an **Add from directory** button and
  administrator recovery through configuration. See
  [Sign-in and accounts](docs/sign-in-and-accounts.md).
- **A saved uSync export** of the shared definitions and starter content, imported on first
  start. See [uSync exports](docs/usync-exports.md).
- **An instance folder** outside the application for settings, databases, media and uSync
  files, so updates replace the application alone. See [Databases](docs/databases.md) and
  [Deployment](docs/deployment.md).
- **Hosting under a sub-path**, such as an IIS application at `/portal`. See
  [Hosting under a sub-path](docs/hosting-under-a-sub-path.md).
- **A backoffice Welcome dashboard** that lists recently changed pages, hooks for branding the
  login screen, and no telemetry reports. See [Login screen and backoffice](docs/backoffice.md).
- **Razor previews in the block editors** from `Umbraco.Community.BlockPreview`. See
  [Block previews](docs/block-previews.md).
- **A testing package** that boots a host on Kestrel with a new database. See
  [Test a host](docs/getting-started.md#test-a-host).

## People picker

The **People picker** (`appPeoplePicker`) is a form companion supplied by AppHost. Add it inside
a form on a Website page and bind its identity and name settings to writable text fields. It
places a search button beside the name field and opens a separate search window. The picker
ships its editor schema, preview, JavaScript and styles with the host.

The published block sets the search OU (or explicitly permits the whole domain), whether to
include child OUs, search and returned LDAP attributes, account filters, optional member group
names and backoffice group aliases. Empty group lists admit anyone who can access the page,
including anonymous visitors on public pages. Otherwise either group list may admit the caller;
no linked Windows SID is required. These settings apply to both search and selection and cannot
be overridden by browser query parameters.

Map returned attributes with one `attribute=formField` pair per line. Each picker owns distinct
writable text fields in its containing form. An optional `mappedField=readOnlyMirrorField`
fallback allows manual input when the directory supplies no value; the form resource must load
the mirror with the directory's value, separately from the typed fallback. Compact-uppercase
fields remove whitespace and uppercase their input. Manual entry can be disabled per picker.

`GET /api/people-picker/{page}/{block}?s=...` searches; `?sid=...` validates a selection within the
same scope. Both responses prohibit caching. The popup uses the validated response for its field
values. At save, AppBlocks' `IFormSubmissionProcessor` hook runs the shared `PeoplePickerService`
again for every new or changed SID. Unchanged saved identities keep their stored directory-owned
fields during an outage, while configured fallback fields remain editable when their mirrors
are empty. Search results and application contact caches do not authorize a new saved identity.

Cached selection is off by default. Opting in permits recent validated people during directory
outages, isolated by page, picker and all settings, with a configured age of 1–1440 minutes and
a process-wide limit of 2,000 people. A live rejection removes that cached identity. The cache
does not survive restarts. Directory calls use the Windows process identity, bounded result
counts and the block's 1–15 second LDAP client/server timeout; live lookup requires a reachable
domain. Search terms and person records are not logged by the picker.

Applications can register `IPeoplePickerSelectionObserver` to store canonical contact data after
save-time validation. They can also check `PeoplePickerService.WasValidated` in their resource
to prevent a changed SID from bypassing the form processor. `IPeoplePickerDirectory` is the
replaceable directory boundary used by tests. The shared types are in `UmbracoAppHost.PeoplePicker`.
Application-specific LDAP filters and attribute mappings belong in the application's block
configuration; no global directory filter is registered.

Run the focused server tests with
`dotnet test --project tests/UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj --filter-class UmbracoAppHost.Tests.PeoplePickerTests --no-progress`.
The browser checks run with `node --test tests/ui/people-picker.browser.test.mjs`, using Playwright
installed by `npm ci` in the sibling `AurorisNet.InteractiveReports` checkout. Override that folder
with `INTERACTIVE_REPORTS_ROOT` and the browser executable with `BROWSER_PATH` if necessary.

## Settings

The library reads its own settings from the `UmbracoAppHost` section and from `InstanceRoot`.

| Setting | Default | What it does |
|---|---|---|
| `InstanceRoot` | `App_Data/UmbracoAppHost` | The instance folder, relative to the content root. It holds `instance.json`, `data/`, `media/` and `uSync/`. The reference website sets `../../reference`, and a site created by `new-site.ps1` sets `..`. |
| `UmbracoAppHost:Samples` | `true` | Registers the `sample.orders` form class, the `orders` report and the sample database, and copies the sample content into a new working uSync folder. Set `false` in a project of your own. |
| `UmbracoAppHost:HostExport` | `uSync` | The project's own committed uSync export, relative to the project root. Its files are copied into a new working uSync folder after the library's. Empty turns it off. |
| `UmbracoAppHost:ProjectRoot` | the content root | The folder that holds the project's own files, such as its uSync export and schema scripts. `builder.ProjectRoot()` returns it. A test site sets it. |
| `UmbracoAppHost:ImportDefinitions` | `true` | Imports the shared definitions of a build at the first start of that build. Set `false` to import them from the backoffice. See [A site takes each build's definitions](docs/usync-exports.md#a-site-takes-each-builds-definitions). |

Settings are read in this order, and a later source replaces an earlier one: the library's
defaults (`src/UmbracoAppHost/Hosting/defaults.json`), the instance's `instance.json`, the
project's `appsettings.json` and `appsettings.<Environment>.json`, environment variables, and the
command line. The library then sets the data folder, the media folder and uSync's root folder
from `InstanceRoot`, so a `uSync:Settings:RootFolder` in a settings file has no effect.

The other sections are described where they apply: `BreakGlass`, `BackOfficeLogin`,
`IdentityServer`, `EmbeddedIdentityServer`, `Accounts` and `Directory` in
[Sign-in and accounts](docs/sign-in-and-accounts.md), `Attachments`, `InteractiveReport` and
`ConnectionStrings` in [Databases](docs/databases.md) and [Interactive Reports](docs/interactive-reports.md), and
`BlockPreview` in [Block previews](docs/block-previews.md).

## Supported versions

`Directory.Packages.props` pins NuGet dependencies. A local clone uses the recorded AppBlocks commit by default.

| Dependency | Release | Notes |
|---|---|---|
| .NET | 10 | The SDK to build, the ASP.NET Core runtime to run a published site. |
| Umbraco | 18.3.2 | AppBlocks pins the same release. The build stops when the two differ. |
| AppBlocks | Submodule commit | AppHost bundles the core, Documents and InteractiveReports assemblies and browser assets. AppHost.Testing bundles the test helpers. |
| uSync | 18.3.1 | |
| `Umbraco.Community.UmbNav` | 5.0.3 | |
| `Umbraco.Community.BlockPreview` | 6.3.0 | |
| `AurorisNet.Umbraco.ApplicationVirtualPathFix` | 18.3.2.1 | Its supported Umbraco range must include the pinned Umbraco release. |
| `AurorisNet.Umbraco.DisableTelemetry` | 18.3.2.1 | |

A new instance keeps Umbraco in a SQLite file. [Databases](docs/databases.md) has the steps to
move it to SQL Server.

## Documentation

| Guide | What it covers |
|---|---|
| [Getting started](docs/getting-started.md) | Running and resetting the reference website, using the library in another repository, and testing a host. |
| [Deployment](docs/deployment.md) | Creating a site from a build, the site folder, IIS, and updating a site. |
| [uSync exports](docs/usync-exports.md) | The saved export and the working folder, saving a backoffice change, and delivering it to existing sites. |
| [Websites, pages and blocks](docs/application-blocks.md) | Web Site and Web Page, templates, menus, Public Access, language fallback, layouts, and the sample pages. |
| [Themes](docs/themes.md) | The two themes, registering another, and packaging a theme as a library. |
| [Sign-in and accounts](docs/sign-in-and-accounts.md) | Administrator recovery, login flows, single sign-on defaults, accounts from the directory, and access to protected pages. |
| [Interactive Reports](docs/interactive-reports.md) | Who administers and reads Interactive Reports in this host, the built-in reports and the Report Page type. |
| [Databases](docs/databases.md) | The database files of an instance, a project's own database, and moving Umbraco to SQL Server. |
| [Hosting under a sub-path](docs/hosting-under-a-sub-path.md) | The virtual path setting and what the host applies it to. |
| [Login screen and backoffice](docs/backoffice.md) | Branding the login screen, the Welcome dashboard and telemetry. |
| [Block previews](docs/block-previews.md) | Razor previews in the block editors and their settings. |
| [Design](docs/design-blocks-and-data-sources.md) | The decisions behind the content model, and which repository owns each part. |
| [Frontend](Frontend/README.md) | Building the theme assets and generated layouts. |

## Developing

Use the .NET 10 SDK, Node 20 or newer and PowerShell 7, with the sibling checkouts listed under
[Start a site](#start-a-site). The .NET build restores and builds the frontend when its sources
change.

```powershell
# Build the library, the reference website and the tests
dotnet build UmbracoAppHost.slnx

# Run the tests, which boot the reference website on the pinned Umbraco release
dotnet test --project tests/UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj

# Check and test the frontend
cd Frontend; npm ci; npm run check; npm test; cd ..

# Pack both packages
dotnet pack src/UmbracoAppHost/UmbracoAppHost.csproj -c Release -o artifacts/packages
dotnet pack tests/UmbracoAppHost.Testing/UmbracoAppHost.Testing.csproj -c Release -o artifacts/packages
./Migrations/Tests/Test-PackageInstall.ps1 (Get-Item artifacts/packages/AurorisNet.Umbraco.AppHost.[0-9]*.nupkg).FullName
```

The repository pins one Umbraco release, so its tests run on that release alone. Three checks run
on a developer's machine and never on GitHub. `deployment/test-sites.ps1` publishes a build,
creates two sites, updates one and starts it, then rehearses a release with a second build. `deployment/test-downstream.ps1` builds a project in a
temporary folder that references the library, and takes an upstream change through the update
routine. `tests/UmbracoAppHost.ProviderTests` runs data sources against PostgreSQL and Oracle when
`UMBRACOAPPHOST_TEST_POSTGRES`, `UMBRACOAPPHOST_TEST_ORACLE` or `UMBRACOAPPHOST_TEST_ORACLE11G`
holds a connection string.

CI and Release fetch AppBlocks `main` once at the start of each run. Both packages use that checkout,
and the job summary records its commit. Rebuilding the same AppHost commit can pick up newer AppBlocks code.
For local development with uncommitted AppBlocks edits, pass `-p:AppBlocksRoot=D:\repo\AurorisNet.Umbraco.AppBlocks`
to `dotnet build`. The workflows set `UseBreakGlassSource`,
`UseIdentityServerSsoSource` and `UseIdentityServerDirectorySource` to `false` to restore
those three libraries at the versions in `Directory.Packages.props`.

The build checks that AppBlocks pins the same `Umbraco.Cms`, `Microsoft.Data.Sqlite` and
`Microsoft.Extensions.FileProviders.Embedded` releases as
`Directory.Packages.props`, and stops the build with both versions when they differ.

## Releases

Dependabot checks the AppBlocks submodule weekly. To update it manually, run
`git submodule update --remote AppBlocks`, test both packages, and commit the updated
`AppBlocks` pointer. After pulling a change, run `git submodule update --init` to use the recorded commit.

Dependabot also checks NuGet each week and groups Umbraco, uSync and the AurorisNet packages into one
pull request. CI tests each update before merging it. The weekly release includes merged AppBlocks
updates and shipped NuGet updates. Both AppHost packages publish under the same version. See the
[release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md).

## License

[MIT](LICENSE)
