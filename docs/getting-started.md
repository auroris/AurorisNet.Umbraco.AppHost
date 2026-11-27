# Getting started

This guide covers three tasks: running the reference website from this checkout, building a
website of your own on the library in another repository, and testing such a host. To publish
sites and update them, see [Deployment](deployment.md).

## Run the reference website

Clone `AurorisNet.Umbraco.AppBlocks` beside AppHost. Building needs the .NET 10 SDK, Node 20 or newer, and the sibling checkouts listed in
[Developing](developing.md).

Open `UmbracoAppHost.slnx` in Visual Studio and start the **Website** launch profile, or run:

```powershell
dotnet run --project src/UmbracoAppHost.Web
```

The first build restores and builds the frontend, and later builds reuse it until its sources
change. The first start creates the instance folder `reference/`, installs Umbraco and imports
the saved uSync export: English and French, the dictionary, the document types and the sample
content. The site answers at `http://localhost:5080/`, where `/` serves the Sample Application.
Later starts keep the database and its content.

For the first sign-in at `/umbraco`, open `reference/instance.json` and use
`UnattendedUserEmail` and `UnattendedUserPassword` under `Umbraco.CMS.Unattended`.
`UnattendedUserName` is the display name. The **Website** development profile also requires the
account's password. See [Sign-in and accounts](sign-in-and-accounts.md) for account recovery.

Every save in the backoffice is exported to `reference/uSync/`. To make a change part of every
new site, copy it into the saved export, as [uSync exports](usync-exports.md#save-a-backoffice-change)
describes.

## Reset the reference website

Stop the website, then run from the repository in PowerShell 7:

```powershell
.\reset.ps1
```

The script deletes the instance's database files, media, sign-in keys, `instance.json` with its
administrator credentials, the working uSync folder and Umbraco's temporary caches. It then
copies the saved export into `reference/uSync/`. The next start installs a new database and
imports it. Source files are left alone.

Before it deletes anything, the script lists the files in `reference/uSync/` that differ from
the saved export and asks whether to discard them. Answer **No**, copy the files you want to
keep into `Migrations/1.0.0-Current/uSync/`, and run it again.

| Parameter | Effect |
|---|---|
| `-Force` | Skips the question. |
| `-WhatIf` | Lists the differences and changes nothing. |
| `-InstanceRoot <folder>` | Resets another instance; see [Reset the instance](#reset-the-instance). |

The script refuses an instance that is running and one whose Umbraco database is not the
instance's own SQLite file.

## Use the library in another repository

A downstream host is a web project in its own repository that references the library. The
library brings Umbraco, both themes, the blocks, the saved export and the views. The host adds
its content, templates, code and settings. Nothing the library ships is edited in the host: a
change to it is made in this repository and brought down.

`deployment/test-downstream.ps1` creates such a host in a temporary folder and takes it through
every step below. Its project is a worked example.

### The project

```text
MyHost\
  .gitignore
  MyHost.slnx
  src\MyHost.Web\
    MyHost.Web.csproj         the project reference to the library
    Program.cs
    appsettings.json          InstanceRoot and the host's settings
    uSync\                    the host's committed export: content, dictionary keys, templates
    Views\                    the host's templates, beside copies of the library's views
      _ViewImports.cshtml     the library's imports, copied when the project is created
    Schema\sqlite\, Schema\sqlserver\   the host's tables, when it has a database of its own
  instance\                   git-ignored: instance.json, data\, media\ and the working uSync\
```

The project file references the library in the checkout of this repository:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CompressionEnabled>false</CompressionEnabled>
    <RazorCompileOnBuild>false</RazorCompileOnBuild>
    <RazorCompileOnPublish>false</RazorCompileOnPublish>
    <CopyRazorGenerateFilesToPublishDirectory>true</CopyRazorGenerateFilesToPublishDirectory>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\AurorisNet.Umbraco.AppHost\src\UmbracoAppHost\UmbracoAppHost.csproj" />
  </ItemGroup>
</Project>
```

The Razor settings publish templates as files and compile them when they are first rendered,
which Umbraco needs for templates edited in the backoffice. In Visual Studio, add the library to
the host's solution with **Add > Existing Project**. AppHost references the sibling AppBlocks, BreakGlass and two IdentityServer projects. Add only AppHost to the solution.

Copy the library's `_ViewImports.cshtml` when you create the project, and commit it. It gives
the Razor editor the shared namespaces and injected services before the first run:

```powershell
New-Item -ItemType Directory -Force src/MyHost.Web/Views | Out-Null
Copy-Item ../AurorisNet.Umbraco.AppHost/src/UmbracoAppHost/Views/_ViewImports.cshtml src/MyHost.Web/Views/_ViewImports.cshtml
```

`new-site.ps1 -Project` creates the file when it is missing and leaves an existing one alone, so
the host can add its own directives.

`Program.cs` starts the site through AppHost so it can serve the setup page if startup checks fail.

```csharp
using UmbracoAppHost;

await AppHostStartup.RunAsync<Program>(args);

public partial class Program : IUmbracoAppHost
{
}
```

The `Program` class holds the host's registrations as static members of `IUmbracoAppHost`, so
`Program.cs` and a test site apply the same ones. A member the host leaves out does nothing.

| Member | Use |
|---|---|
| `ConfigureServices(WebApplicationBuilder)` | Registers the host's services, before the library's. |
| `ConfigureConnections(WebApplicationBuilder, AppBlocksBuilder)` | Registers the host's connections, such as [a database of its own](databases.md#a-database-of-your-own). |
| `ConfigureMiddleware(IUmbracoApplicationBuilderContext)` | Adds middleware inside Umbraco's pipeline, after Umbraco's own. |
| `ConfigureEndpoints(IUmbracoEndpointBuilderContext)` | Maps the host's endpoints after Umbraco's. |
| `CheckNewInstallation(WebApplicationBuilder, AppHostStartupReport)` | Checks settings before the instance data has been initialized. It can report missing settings even when the site account cannot create its files. |

Register site-specific checks in `ConfigureServices` with
`builder.Services.AddAppHostStartupCheck<CaseDatabaseCheck>()`. Implement `IAppHostStartupCheck`
on the class. AppHost resolves its dependencies from a scope and calls
`Task CheckAsync(AppHostStartupReport report, CancellationToken cancellationToken)` before Umbraco boots.
Use read-only operations that work before installation. For example, a database check can inspect
the effective connection and the current database user's default schema. Opening a connection through
a factory that runs migrations on open would start the installation during the check.

Call `report.AddProblem(description, remedy)` for each failed requirement and continue with the
independent checks. `AddAdvice` adds a nonblocking issue to the guide when another problem stops
startup. Write both strings for the person installing the site. Include the file, setting, and
steps to fix it. Log exception details privately with the check's injected logger. Everyone who
visits the site can read the guide.

Hosts that manage the ASP.NET Core lifecycle themselves can use `builder.AddUmbracoAppHost()`
and `app.UseUmbracoAppHostAsync()`. These calls run the checks and propagate startup exceptions.
The independent HTTP responder is provided by `AppHostStartup.RunAsync`.

A host that builds Umbraco itself can call the overload on `IUmbracoBuilder` after
`AddComposers()`: `AddUmbracoAppHost(configuration, configureConnections)`. It registers the
shared services without the instance folder and without the library's default settings, so
the host supplies both.

`appsettings.json` holds what can be committed:

```json
{
  "InstanceRoot": "../../instance",
  "Attachments": {
    "Connection": "MyHostDb"
  }
}
```

- `InstanceRoot` is relative to the project folder. `../../instance` puts the instance at the
  root of the repository, so builds and publishes never pick up its files.
- `Attachments:Connection` keeps record attachments in the host's own database. Leave it out to
  keep them in `Attachments.sqlite.db` in the instance.

The host's `appsettings.json`, environment variables and command line replace the library's
defaults. Secrets and connection strings for a server go in that server's `instance.json`.

`.gitignore`:

```gitignore
bin/
obj/
.vs/
instance/
umbraco/
appsettings-schema*.json
umbraco-package-schema.json
```

### Build and run

The library finds its sibling checkouts beside the checkout of this repository. When one is
elsewhere, pass its folder to every build, or set the property as an environment variable
before starting Visual Studio:

```powershell
dotnet build src\MyHost.Web -p:BreakGlassRoot=D:\src\AurorisNet.Umbraco.BreakGlass
```

Start the host from Visual Studio or with `dotnet run --project src\MyHost.Web`. The first start
creates the instance folder, installs Umbraco and imports the working uSync folder. For the first
sign-in at `/umbraco`, use `UnattendedUserEmail` and `UnattendedUserPassword` from the
`Umbraco.CMS.Unattended` section in `instance/instance.json`.

### The host's export and the library's

Before the first import, the host fills the working folder `instance/uSync/` from the library's
export and then from the host's own, as [uSync exports](usync-exports.md#first-start) describes.
A host file at a path the library ships is ignored.

The project's `uSync/` folder holds the host's own files only: its content, its dictionary keys
(prefix them with the host's name) and the registrations of its templates. Make them in the
backoffice, where uSync writes every save to `instance/uSync/`, and copy the files to the same
relative paths in the project's `uSync/`. Leave out `usync.config` and the library's
definitions. A template is its registration under `Templates/` plus its view in `Views/`.

At every start the host rewrites the views the library ships when the copies in `Views/` differ,
so an upstream change to a view arrives with the rebuild. Commit the copies with the upstream
change that brought them, unless the host embeds its own `Views/`: such a host is a theme library
itself, so it embeds and publishes only its own views and leaves the copies out of commits, or
they would ship as its own. The host's other views are left alone.

### Bring an upstream change down

1. Pull this repository, update the sibling checkouts including AppBlocks, and rebuild the host. Code, browser assets
   and the library's export arrive through the project reference, and the shipped views are
   rewritten at the next start.
2. Start the host. The start writes the definition files that changed (`ContentTypes`,
   `DataTypes`, `Dictionary`, `Languages`, `MediaTypes`, `MemberTypes`, `RelationTypes` and
   `Templates`) into the working folder, removes the folder's copies of the definitions AppBlocks
   installs, and imports those folders. The log lists what it wrote and what the import changed.
   It leaves `Content/` alone.
   [A site takes each build's definitions](usync-exports.md#a-site-takes-each-builds-definitions)
   describes the start.
3. Run the host's tests, and fix the pages an upstream change broke.
4. Redeploy.

For a host that keeps running, `.\refresh-definitions.ps1 -Instance <repository>\instance` copies
the same files, and **Settings → uSync** imports them: **Report** to review the changes, then
**Import**. `-WhatIf` lists without changing anything. The script reads
`instance/.apphost/runtime.json`, which every start writes, so the host must have started once
on the instance. It stops when the working folder has never been filled, because the next start
fills it from the current export.

The host's own database is outside this routine: its schema scripts run at the next open.

### Reset the instance

Stop the host, then run from the checkout of this repository:

```powershell
.\reset.ps1 -InstanceRoot <repository>\instance
```

The script lists the working files that differ from what the next start would copy and asks
before discarding them. It compares a file at a path the host's export ships with the host's
copy, and any other file with the library's export. It then deletes the database, media,
sign-in keys, `instance.json`, the working uSync folder and Umbraco's temporary caches, and
copies nothing: the next start fills the working folder from both exports, installs a new
database and creates new administrator credentials.

### Publish it as a site

`new-site.ps1 -Project` publishes the host in place of the reference website and creates a site
from the build:

```powershell
pwsh -File ./new-site.ps1 -Project <repository>\src\MyHost.Web\MyHost.Web.csproj -Name MyHost -Destination E:/Sites/MyHost
```

Update the site with `update-site.ps1 -Site <site> -Project <project file>`.
[Deployment](deployment.md#publish-another-project) describes the parameters.

When the host has schema scripts, add
`<None Update="Schema\**\*.sql" CopyToPublishDirectory="PreserveNewest" />` to its project file
so the published site carries them.

## Test a host

`AurorisNet.Umbraco.AppHost.Testing` (`tests/UmbracoAppHost.Testing`) boots a host the way its
`Program.cs` does, on a free loopback port with a new database and instance folder under a
temporary directory, and waits for the unattended install. It signs in members and backoffice
users and hands out clients that drive forms, attachments, tabular rows and reports over HTTP.
The clients and `TestSiteException` come from the `AppBlocks.Testing` assembly bundled in `AurorisNet.Umbraco.AppHost.Testing`. These helpers work with any test framework.

### Set up the test project

Add `public partial class Program { }` to the host's `Program.cs` so tests can name its
assembly. A host that implements `IUmbracoAppHost` on `Program` already has it.

Reference the host and the testing package, at the same version as
`AurorisNet.Umbraco.AppHost`. A start with a testing package of another version fails with a
message that names the mismatch.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.3" />
    <PackageReference Include="AurorisNet.Umbraco.AppHost.Testing" Version="1.0.0-alpha.1" />
    <ProjectReference Include="..\MyHost\MyHost.csproj" />
    <Using Include="Xunit" />
    <Using Include="UmbracoAppHost.Testing" />
    <Using Include="AppBlocks.Testing" />
  </ItemGroup>
</Project>
```

A host that builds the library from a checkout references the testing project in place of the
package:

```xml
<ProjectReference Include="..\..\..\AurorisNet.Umbraco.AppHost\tests\UmbracoAppHost.Testing\UmbracoAppHost.Testing.csproj" />
```

Move the host and its tests to packages in one change. A project reference to
`UmbracoAppHost.csproj` beside a package reference to the testing package brings
`UmbracoAppHost.dll` from both.

Select Microsoft Testing Platform in the repository's `global.json`, then run `dotnet test`
from the solution folder, or `dotnet test --project path/to/MyHost.Tests.csproj`:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

### Write a test

A fixture starts one site for a class of tests. This one signs in a member, saves an order
through the sample Orders form and reads the `orders` report:

```csharp
using AppBlocks.Blocks;
using UmbracoAppHost.Testing;
using Xunit;

public sealed class SiteFixture : IAsyncLifetime
{
    public TestSite Site { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Site = await TestSite.StartAsync(new TestSiteOptions
    {
        Application = typeof(Program).Assembly,
    });

    public async ValueTask DisposeAsync() => await Site.DisposeAsync();
}

[Collection("Sites")]
public sealed class OrdersTests(SiteFixture fixture) : IClassFixture<SiteFixture>
{
    // The page and its form block; a host's own come from its saved export.
    private static readonly Guid OrdersFormPage = AppBlockSchema.Key("sample:orders-form");
    private static readonly Guid OrderForm = AppBlockSchema.Key("sample:form");

    [Fact]
    public async Task A_member_saves_an_order_and_reads_the_report()
    {
        var site = fixture.Site;
        var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot");
        using var browser = await site.SignInMemberAsync(member);

        var form = site.Forms(browser, OrdersFormPage, OrderForm);
        Assert.True((await form.LoadAsync()).IsSuccess);
        var saved = await form.CreateAsync(new { customer = "Test order", region = "NORTH", status = "NEW", amount = "12.50", orderDate = "2026-10-29" });
        Assert.True(saved.IsSuccess, saved.Body.GetRawText());

        var rows = await site.Reports(browser).RowsAsync("orders");
        Assert.NotEmpty(rows.EnumerateArray());
    }
}
```

**A test process runs one site at a time.** Umbraco keeps state in static fields, which starting
and disposing a site reset. `TestSite.StartAsync` throws `TestSiteException` while another site
of the process is running. Put every class that starts a site in the same collection, and run
that collection without parallelization.

| `TestSite` member | Use |
|---|---|
| `CreateMember`, `CreateUserAsync`, `SetPasswordAsync`, `LinkWindowsIdentity` | Create accounts and link a Windows SID. |
| `SignInMemberAsync`, `SignInBackOfficeAsync` | A client with the member cookie or the backoffice cookie. |
| `SignInManagementApiAsync`, `SignInAsAdministratorAsync` | A client with a management API token. |
| `Forms`, `Attachments`, `Tabular`, `Reports`, `DownloadPaperReportAsync` | Clients that call the block endpoints as the browser runtime does. |
| `GetPageAsync`, `CreateClient`, `WaitForPublishedAsync`, `CreateScope`, `Services` | Pages, plain clients and the site's services. |
| `Address`, `PathBase`, `ProjectFolder`, `InstanceRoot`, `DataRoot`, `USyncFolder` | Where the site runs and keeps its files. |

The clients also work against any running site: `new FormsClient(client, pathBase, page, form)`
and the other `AppBlocks.Testing` clients take an `HttpClient` and the site's virtual path.

### What the site reads from the host project

`TestSite.StartAsync` finds the host project by walking up from the host assembly's folder in
the test output: the first folder that holds `<assembly name>.csproj`, or else the one project
of that name under the first folder with a `*.slnx`, a `*.sln` or `.git`, outside `bin`, `obj`,
`node_modules` and folders whose names start with a dot. Set `TestSiteOptions.ProjectFolder`
when that does not find it. From the project folder the site:

- reads `appsettings.json` and `appsettings.<EnvironmentName>.json` (`Development` unless
  `TestSiteOptions.EnvironmentName` says otherwise). It ignores the project's
  `BreakGlass:AdminPassword` and `UmbracoAppHost:AdminPassword`, so the administrator keeps
  `TestSiteOptions.AdminPassword`. The site's own settings follow: the instance folder, the
  Umbraco database, HTTP, and uSync's startup import and export. `TestSiteOptions.Settings`
  comes last;
- sets `UmbracoAppHost:ProjectRoot` to the project, so the project's committed export is
  imported on first boot and `builder.ProjectRoot()` returns the project folder;
- copies the project's `Views/` into the content root, which is a temporary folder;
- applies the host's `IUmbracoAppHost` registrations, from the `Program` class or else the one
  class of the host's assembly that implements the interface. `TestSiteOptions.Host` names
  another. The host's `RunAsync` never runs.

A project whose settings declare `Kestrel:Endpoints` or `Urls` replaces the free loopback port.
The site then binds the configured address, and the start fails when there is more than one.
Replace those settings through `TestSiteOptions.Settings`.

### Options

| `TestSiteOptions` | Default | Use |
|---|---|---|
| `Application` | required | The host's assembly, such as `typeof(Program).Assembly`. |
| `ProjectFolder`, `Host` | found | The host project's folder and its `IUmbracoAppHost` class. |
| `Root` | a new temporary folder | The content root and the parent of the instance folder. |
| `UseInstallSnapshot` | `true` | Starts from a copy of an install made once in the process for these options. |
| `EnvironmentName` | `Development` | The hosting environment. |
| `VirtualPath` | none | `Umbraco:CMS:Hosting:ApplicationVirtualPath`, such as `/portal`. |
| `AdminPassword` | a fixed test password | The administrator's password. |
| `FirstBootGroup` | `All` | `Settings` imports the export's definitions without its content. |
| `Settings` | empty | Further configuration, applied last. |
| `ConfigureBuilder` | none | Registers services before the host's and the library's. |
| `ConfigureConnections` | none | Registers further connections after the host's. |
| `ConfigureApp` | none | Maps test-only endpoints after `UseUmbracoAppHostAsync`. |
| `TestLoginRoute` | `true` | Maps the route `SignInMemberAsync` uses to sign in without a password. |
| `WaitForAdministratorRename` | `true` | Waits until the administrator has its configured username. |
| `InstallTimeout` | 3 minutes | How long to wait for the unattended install. |

The install and uSync's first import take most of a new site's time, so a test process does
them once for each set of options and gives every site its own copy, deleted with the site. A
site that sets `Root` or `ConfigureConnections`, or sets `UseInstallSnapshot` to `false`,
installs for itself. `ConfigureBuilder` runs on the copy only, so a registration that the first
import depends on also needs `UseInstallSnapshot = false`.

### Other test runners

The library's defaults exclude assemblies whose names start with `xunit.` from Umbraco's type
scan, because xUnit puts runner assemblies beside the test executable. For another runner, add
its assembly prefixes through `TestSiteOptions.Settings` at an index of 10 or more, such as
`Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:10`. A lower index replaces one of
the library's entries.
