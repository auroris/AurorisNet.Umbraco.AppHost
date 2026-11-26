# Deployment

This guide covers publishing the application, creating independent sites from the build, and
updating them. Every site created from one build runs the same application. Sites differ in
their database content and instance settings.

Run the scripts with PowerShell 7 (`pwsh`). Windows PowerShell 5.1 cannot run them, so keep the
`pwsh -File` prefix when starting them from that shell. Publishing needs the .NET 10 SDK,
Node 20 or newer and the sibling checkouts the repository's README lists. A site needs only the
ASP.NET Core 10 runtime.

## Create a site

```powershell
pwsh -File new-site.ps1 -Name MyWebsite -Destination E:/Sites/MyWebsite
pwsh E:/Sites/MyWebsite/start-site.ps1
```

`new-site.ps1` publishes `src/UmbracoAppHost.Web` in Release configuration to
`artifacts/builds/<timestamp>-<commit>/` and creates the site from that build:

- `app/` receives the published application, with `InstanceRoot` in its `appsettings.json` set
  to the site folder.
- `instance.json` receives a new site identity, image signing key and administrator password,
  an HTTPS address on a random local port, and `Umbraco:CMS:Hosting:Debug` set to `false`.
- `uSync/` receives the saved uSync export.

The first start installs Umbraco and imports that folder. Sign in at `/umbraco` as `admin` with
the password in `instance.json`, under `Umbraco.CMS.Unattended.UnattendedUserPassword`.

| Parameter | Use |
|---|---|
| `-Name` | Required. The site's name: letters, digits, `_` and `-`, starting with a letter or `_`. |
| `-Destination` | The site folder, which must not exist yet. Default: a folder named after the site in the current folder. |
| `-Build <folder>` | Creates the site from an existing build, without publishing. |
| `-Project`, `-Assembly`, `-MsBuildProperties` | Publish another project; see [Publish another project](#publish-another-project). |

Create every site with `new-site.ps1`. A copy of a site that has run carries its identity,
members, data and credentials with it.

### The build folder

A build folder is self-contained. Beside `app/` it holds copies of `new-site.ps1`,
`update-site.ps1`, `refresh-definitions.ps1` and the `deployment/` folder, and `build.json`,
which records the build id, the Umbraco version, the published project, its application
assembly and a hash of every file.

`build.json` also records what the build was made from. `sources` lists the checkout of the
published project and of every project it builds from source, such as sibling checkouts of this
library and AppBlocks. Each entry has the checkout's folder name, its commit, and
`workingTreeChanges`, which is `true` when files in it were changed or added since that commit.
`committed` is `true` when no checkout had such changes. A site's `.apphost/instance.json`
keeps the `sources` of the build it runs.

Release a build whose `committed` is `true`, so that its commits say exactly what it holds. The
scripts warn when they publish or install a build that holds uncommitted changes. They find
`git` on the `PATH` or in GitHub Desktop's installation, and a build made where neither is
there records no sources.

Pass `-Build <folder>` to create or update several sites from one publish. To work on a machine
without the checkout, copy the folder there and run the scripts inside it.

### Publish another project

A web project in another repository that references the library is published and replicated the
same way, and its sites run its own application.

| Parameter | Use |
|---|---|
| `-Project <project file>` | Publishes that project in place of `src/UmbracoAppHost.Web`. |
| `-Assembly <file>` | The application's file name, when it is not the project's name with `.dll`. |
| `-MsBuildProperties` | Properties for `dotnet publish`, such as `'-p:BreakGlassRoot=D:\src\AurorisNet.Umbraco.BreakGlass'`. They come after the script's own, so `-p:Configuration=Debug` replaces Release. |

The three apply when the script publishes. They are refused with `-Build` and inside a build
folder.

Before publishing, the script copies the library's `Views/_ViewImports.cshtml` into the project
when the project has none. Commit that file with the project. The build's `app/Views/` receives
the library's own copies of the views it ships, whatever the project folder held, so a stale
copy never reaches a site.

When the published `appsettings.json` sets `UmbracoAppHost:Samples` to `false`, the site's
`uSync/` folder starts empty. The first start fills it with the library's export without the
samples, then with the project's own export, which the publish places in `app/uSync/`.

## The site folder

| Location | Contents |
|---|---|
| `app/` | The published application, identical in every site created from the build. |
| `app/appsettings.json` | Settings the operator can edit. Updates keep the file. |
| `instance.json` | The site's identity, credentials and settings. |
| `data/` | The Umbraco database, the application databases and the sign-in keys. |
| `media/` | Uploaded media. |
| `uSync/` | The site's uSync folder: the saved export at creation, then the site's own exports and refreshed definitions. |
| `start-site.ps1` | Starts the site with Kestrel. IIS does not use it. |
| `.apphost/` | The installed build's file list, the run lock and update backups. |

SQLite paths are resolved from the site folder at run time, so a stopped site can be moved as a
whole. Put connection strings for other databases, identity server settings and secrets in
`instance.json` or in environment variables. A setting in `app/appsettings.json` replaces the
same setting in `instance.json`.

`start-site.ps1` runs the application recorded in `.apphost/instance.json` with Kestrel, at the
`Urls` address in `instance.json`. Pass `-Urls <address>` to use another.

The application holds `.apphost/run.lock` while it runs, under Kestrel and IIS alike. A second
start, an update or a reset of the same site stops with a message while the lock is held.

## Host a site in IIS

1. Install the .NET 10 Hosting Bundle on the server.
2. Create the IIS site or application with `app/` as its physical path. The application finds
   its data through `InstanceRoot`, which already names the site folder.
3. To sign people in with their Windows accounts through the embedded identity server, enable
   **Anonymous Authentication** and **Windows Authentication** on the application. Then run the
   single sign-on package's helper from an elevated PowerShell on the server, with the IIS
   site's name and the application's path (`/` for a site's root application):

   ```powershell
   .\tools\configure-iis-authentication.ps1 -Site 'Default Web Site' -ApplicationPath '/portal'
   ```

   The helper is in the `tools` folder of the
   [`AurorisNet.Umbraco.IdentityServer.SSO`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO)
   repository. It keeps Windows authentication for sign-in and turns IIS's Windows challenge off
   under the application's `umbraco` path, where Umbraco authenticates. It supports `-WhatIf`.
   The settings are stored in IIS configuration and stay through application updates. The
   package's [IIS reference](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO/blob/main/docs/EMBEDDED-SERVER.md#iis-windows-authentication)
   explains each change.
4. For an application under a path, set `Umbraco:CMS:Hosting:ApplicationVirtualPath` to that
   path, such as `/portal`.

Stop the application pool before updating the site. Databases and media stay outside the
application's physical path.

To choose or reset the administrator's password, set `BreakGlass:AdminPassword` in
`app/appsettings.json` and save. The running site applies it without an IIS restart, unlocks the
account and removes its two-factor enrolment. Remove the setting after signing in.

## Update a site

```powershell
pwsh update-site.ps1 -Site E:/Sites/MyWebsite -WhatIf
# Stop the running site, then:
pwsh update-site.ps1 -Site E:/Sites/MyWebsite
pwsh E:/Sites/MyWebsite/start-site.ps1
```

Upgrade Umbraco and the packages in the checkout, check the result in the reference website,
then update the sites. Take a backup before updating a production site. `update-site.ps1` then:

1. Takes the instance lock, resolves an interrupted update and checks the installed files.
   Operators may customize `app/appsettings.json`. Razor template hashes use Umbraco's newline
   and byte-order-mark normalization.
2. Publishes the application or reads the supplied build. Creation and updates compare the
   incoming application's paths and hashes to `build.json`, then verify the staged copy before
   applying site settings. A missing, changed or unlisted file stops installation.
3. Copies the site's settings and files created through Umbraco into the staged app. A conflict
   with a newly shipped file stops the update. Stages the working uSync folder with the incoming
   shared definitions and removes the staged copies of definitions AppBlocks owns.
4. Moves the previous app to `.apphost/backups/`, installs the staged app and uSync folder,
   then atomically replaces the installed inventory. A failure before that last step restores
   the previous files. If the process stops during the switch, keep the site stopped and rerun
   the update. The updater uses its pending record to recover before applying the build.

`-WhatIf` checks installed files and verifies an explicitly supplied or bundled build. With
`-Project` it skips publishing. Previews leave a pending update for the actual update to recover.

Settings, databases, media, sign-in keys and content stay as they are. Keep custom source out
of `app/`: make changes in the checkout and publish them.

The site imports the build's definitions at its next start, before it serves a page, and records
them. An import that fails stops the start, so read the log of the first start after an update.
`Content/` is not copied, because pages belong to the site after it is created.
[A site takes each build's definitions](usync-exports.md#a-site-takes-each-builds-definitions)
describes the start and how to import by hand instead.

| The script stops when | Remedy |
|---|---|
| The site is running | Stop it, or stop its application pool. |
| The build runs another application than the site | Update with `-Project` and the site's own project, or `-Build` and a build of it. |
| The build's Umbraco version is older than the site's | Restore a matching database backup; do not downgrade. |
| The build's Umbraco major version differs | Plan the major upgrade separately. |

Database upgrades run at the site's next start. For a rollback after that point, restore the
pre-update site backup, including its working uSync folder and installed inventory, together
with the matching databases. The directory under `.apphost/backups/` holds the previous
application files.

`update-site.ps1` takes the same `-Build`, `-Project`, `-Assembly` and `-MsBuildProperties` as
`new-site.ps1`.

### Refresh an instance run from a project

`refresh-definitions.ps1 -Instance <folder>` does the uSync part of an update for an instance
that a host runs from its own project, such as a development instance after the host was
rebuilt against a newer library. It reads the instance's `.apphost/runtime.json`, copies the
definition folders of the export in the host's build output over the instance's `uSync/` folder,
removes the copies of AppBlocks' definitions and lists what changed. `-WhatIf` only lists. A host
does the same by itself at its next start and imports the definitions then. Run the script for a
host that keeps running, and import from **Settings → uSync** in its backoffice.

## Verify the scripts

| Script | What it checks |
|---|---|
| `deployment/test-sites.ps1` | Publishes a build, or takes `-Build`. Creates two sites in a temporary folder, starts both, updates one, checks what the update keeps and refreshes, and starts the updated site. Run from the checkout, it then rehearses a release: it publishes a second build with one changed dictionary text, updates the site with it, and checks that the started site shows the new text. |
| `deployment/test-startup.ps1 -Project <project file> -ContentRoot <temporary folder> -Reset` | Starts a built host over HTTP on a separate instance and runs the reset script against it. |
| `deployment/test-downstream.ps1` | Creates a project that references the library in a temporary folder, with its own export, template, data source and paper report. Starts it without the samples, delivers a changed library view and definition through a rebuild and `refresh-definitions.ps1`, then resets the instance. |

`test-sites.ps1` and `test-downstream.ps1` take `-MsBuildProperties`.
