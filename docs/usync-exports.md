# uSync exports

The library ships a saved uSync export that a new site imports on its first start. This page
describes the export and the working folder, how to save a backoffice change into the export, and
how to deliver it to sites that already exist.

## The two folders

| Folder | Purpose |
|---|---|
| `Migrations/1.0.0-Current/uSync/` | The saved export, in source control. The library embeds it and copies it to the build output, so every host and build carries it. |
| `<instance>/uSync/` | The working folder of one instance: `reference/uSync/` for the reference website, `uSync/` in a site folder. uSync imports from it and exports every backoffice save to it. |

The host sets uSync's root folder to `<InstanceRoot>/uSync` after it has read the settings
files, so `uSync:Settings:RootFolder` in `appsettings.json` has no effect.

The library's defaults for uSync are:

| `uSync:Settings` | Default | Effect |
|---|---|---|
| `ExportOnSave` | `All` | Every save in the backoffice writes the item's file to the working folder. |
| `ImportOnFirstBoot` | `true` | A new database imports the working folder once. |
| `FirstBootGroup` | `All` | The first import takes definitions and content. |
| `ImportAtStartup`, `ExportAtStartup` | `None` | uSync's own startup import and export stay off. |

uSync records its first boot in the database and imports the whole folder only then. At later
starts the host imports the shared definitions of a build the site has not imported yet. See
[A site takes each build's definitions](#a-site-takes-each-builds-definitions).

## What the export holds

The saved export is an ordinary uSync export: `usync.config` beside `Content/`, `ContentTypes/`,
`DataTypes/`, `Dictionary/`, `Languages/`, `MediaTypes/`, `MemberTypes/`, `RelationTypes/` and
`Templates/`. Files keep their exported names and keys, so a file copied from a working folder
needs no editing.

- **Page types and templates.** Web Site, Web Page and their compositions, Report Page, and the
  template registrations. The Razor files themselves are in the library's `Views/` folder. In the
  backoffice, these types and the website grid are in the **Website** folder of Document Types
  and Data Types; Report Page and Paper report are in **Application**.
- **Languages.** English (`en`, the default) and French (`fr`, falling back to English).
- **Dictionary.** The 15 keys the templates use, each with an English and a French value.
  `Search URI` is `#` in both languages until a site supplies its search address.
- **Content.** The Data sources root with the **Recently changed pages report** definition. The
  samples (the Sample Application, the Website theme demo, and the Orders data sources, report
  definitions and lists) are in the reference launcher's own export,
  `src/UmbracoAppHost.Web/uSync/`. [Websites, pages and blocks](application-blocks.md#sample-pages)
  lists them.

The export assigns no domains, and its pages have English content only. `/` serves the first
root page by sort order, which is the Sample Application, so keep the sort orders when adding
root pages. Assign domains to a Web Site to publish its languages at their own addresses.

Accounts, Public Access rules, application databases and uploaded media are outside the export.
Configure Public Access in each installation.

### Definitions that belong to AppBlocks

The form, field, layout and content blocks, the Interactive Report block, and the data source,
list and report definition types come from the `Schema/` folders of AppBlocks and its companions.
They are in the **AppBlocks** folder of Document Types and Data Types. AppHost registers the
People picker with the same schema installer. Two rules apply to uSync imports.

- Before any uSync import, `AppBlocksDefinitionImport` installs the definitions that are missing,
  so imported content finds its types on a first start. When that fails, the import stops. uSync
  then records the first boot as done, so reset the instance after fixing the cause.
- During an import, the host skips a file whose key is one of those definitions. A copy in a
  working folder therefore never replaces what AppBlocks installed.

Change those definitions in AppBlocks. A copy of one in the saved export fails
`DefinitionOwnershipTests`. Two definitions are in both places on purpose, listed in
`AppBlocksDefinitions.HostOwned`:

| File | What the host's copy adds |
|---|---|
| `DataTypes/ApplicationApplicationBlocks.config` | AppBlocks' Forms container grid, with the blocks a page grid offers here: Interactive Report, HTML, CSS and JavaScript. |
| `ContentTypes/paperreport.config` | The Paper report type with this host's default template and its place in the tree. |

When an AppBlocks schema changes either one, the tests fail until the host's copy carries the
change.

The library's defaults set `AppBlocks:Definitions:UpdateAtStartup` to `true`. A start therefore
resets each AppBlocks definition that the build changed since it was installed on the site, and
the log names them. The two definitions in the table are registered as the site's own, so the
export's copies stay as uSync imported them, at a start and when an administrator runs
**Settings → AppBlocks → Definitions → Reinstall**.

## First start

A working folder without `usync.config` has never been filled. Before uSync's first import, the
host fills it in this order and never replaces a file that is already there:

1. The library's export.
2. The project's own export, the folder `UmbracoAppHost:HostExport` names (default `uSync` in the
   project root), for every path the library does not ship.
3. The embedded `uSync/` files of each registered [theme library](themes.md#a-theme-library), for
   paths that neither of the first two ships.

`new-site.ps1` copies the build's `app/uSync/` folder, which holds the library's export and the
project's own, into each new site's working folder. `reset.ps1` copies the saved export into
`reference/uSync/` for the reference website.

The host also writes the views the library ships to the project's `Views/` folder before the
import, so Umbraco finds a Razor file for every template registration. At each later start it
rewrites a shipped view that differs from the library's; when theme libraries ship a view of the
same name, the last registered one's is the only one written. `_ViewImports.cshtml` is written only
when it is missing, and a project's other views are left alone.

Umbraco's installer creates English, so that its content cache can start before uSync runs. Set
`Umbraco:CMS:InstallDefaultData:Languages` to install other languages. uSync then imports the
saved language definitions, French included.

A failed first import is reported in uSync's log. Fix the exported files and
[reset the instance](getting-started.md#reset-the-reference-website) to run the first boot again.

## Save a backoffice change

For a **project's own saved export**, save the change in the development backoffice and run
these commands from the AppHost checkout. The first lists differences. The second copies the
selected working files, byte for byte, to the project's configured export.

```powershell
./capture-usync.ps1 -InstanceRoot D:/Sites/dev-instance
./capture-usync.ps1 -InstanceRoot D:/Sites/dev-instance -Path 'Dictionary/office-title.config','Templates/officelist.config'
```

Use the instance created by running the project from its checkout. Review the result in Git
and include the corresponding Razor view for a template. A project that uses its own installer
for forms or pages, such as RDAO, continues to maintain those items in its installer.

The command reads the files uSync has already exported. It can promote existing project files
and new shared-definition files, including dictionary entries and templates. New content and media
are installation-specific and require an explicit source decision outside the command.
Items shipped by another assembly, conflicting keys, and deletion or rename markers are listed for
review. A selected template needs a project view that Git can include. Use `-WhatIf` to preview a
selection. uSync continues to handle export on save and import into the database.

For a **shared AppHost library definition**:

1. Make the change in the reference backoffice. uSync writes the item's file to
   `reference/uSync/`.
2. Copy that file to the same relative path in `Migrations/1.0.0-Current/uSync/`. When you copy the
   whole folder, leave out the paths that `deployment/appblocks-definitions.txt` lists, which
   uSync also writes there. Delete a file from the saved export when its item was deleted on
   purpose, because a copy leaves old files in place.
3. Review the change and commit it with any related code or view changes.

`reset.ps1` compares the working folder with the saved export before it deletes anything. It
lists the files that were changed, added or removed in the working folder and asks before
discarding them. The comparison leaves out AppBlocks' definitions and
`ContentTypes/paperreport.config`, whose allowed templates the host rewrites at run time.

## A site takes each build's definitions

Each start brings the site the definitions of the build it runs, before the site serves a page:

1. **AppBlocks' definitions.** AppBlocks installs the ones that are missing and resets the ones
   the build changed since they were installed. Both need
   `Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations`, which is on by default.
2. **The shared definitions of the saved export.** These are the files in `ContentTypes`,
   `DataTypes`, `Dictionary`, `Languages`, `MediaTypes`, `MemberTypes`, `RelationTypes` and
   `Templates`, from the library's export, the host's and each theme library's, in the order a
   [first start](#first-start) fills the working folder. The host keeps a fingerprint of the ones
   the site last imported, in Umbraco's key-value table under `UmbracoAppHost:SharedDefinitions`.
   When the build ships other ones, the host writes the files that differ into the working
   folder, imports those eight folders and records the new fingerprint. Before the import it
   removes two kinds of file that uSync's export on save leaves there: a second file for an item
   the build ships, such as one under the name the item had before it was renamed, and the
   copies of AppBlocks' definitions.
3. **The blocks registered from code** are allowed in their Block Grids again, because the import
   put those data types back as the export has them.
4. **`DefinitionsReadyNotification`** is published, and an application
   [installs its content](#install-content-when-the-definitions-are-ready).

A start whose build ships the definitions the site already imported skips the import. A new site
imports once after its first boot, which takes the dictionary only when it takes content.

| On the site | What a build's definitions do to it |
|---|---|
| A shared definition changed in the site's backoffice | The next build that ships other shared definitions replaces it with the build's. Change shared definitions in the saved export |
| `Content/` | Stays. Pages belong to the site after it is created. Copy a content file by hand when a site should receive it |
| A definition file the site added | Stays, and is imported with the rest |
| A definition the export no longer ships | Stays on the site until someone deletes it in the backoffice |

**When an import fails**, the site does not start. The log names each definition with uSync's
reason. The host puts the working folder's files back, records nothing, and imports again at the
next start, so a corrected build starts from the files the site had. What the failed import had
changed in the database stays changed, so restore the database backup taken before the update
when the site must return to the earlier build.

**To import by hand**, set `UmbracoAppHost:ImportDefinitions` to `false`. A start then imports
nothing. `update-site.ps1` and `refresh-definitions.ps1` copy the build's definition folders into
the working folder, and an administrator runs **Report** and **Import** in **Settings → uSync**.

### Install content when the definitions are ready

An application that installs pages from code handles `DefinitionsReadyNotification`. The host
publishes it at each start once the definitions above are on the site, and before the site
serves a page. A handler that throws stops the start: the site does not come up, and its log
carries the error.

```csharp
public sealed class NoticePages(IServiceScopeFactory scopes) : INotificationAsyncHandler<DefinitionsReadyNotification>
{
    public async Task HandleAsync(DefinitionsReadyNotification notification, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ContentStepRunner>().RunAsync("Notices", InstallAsync, Steps, ct);
    }
}

// Among the host's registrations:
builder.AddNotificationAsyncHandler<DefinitionsReadyNotification, NoticePages>();
```

AppBlocks' `ContentStepRunner` installs the pages on a new site and runs each later change once on
an updated one.
[Pages that change between releases](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/site-integration.md#pages-that-change-between-releases)
describes it. Umbraco publishes its own started notification once the site is ready to process
requests, so that one suits work the site can run without.
