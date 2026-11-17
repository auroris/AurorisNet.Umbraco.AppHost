# uSync exports

The library ships a saved uSync export that a new site imports on its first start. This page
describes the export and the working folder, how to save a backoffice change into the export, and
how to deliver it to sites that already exist.

## The two folders

| Folder | Purpose |
|---|---|
| `src/UmbracoAppHost/uSync/` | The saved export, in source control. The library embeds it and copies it to the build output, so every host and build carries it. |
| `<instance>/uSync/` | The working folder of one instance: `reference/uSync/` for the reference website, `uSync/` in a site folder. uSync imports from it and exports every backoffice save to it. |

The host sets uSync's root folder to `<InstanceRoot>/uSync` after it has read the settings
files, so `uSync:Settings:RootFolder` in `appsettings.json` has no effect.

The library's defaults for uSync are:

| `uSync:Settings` | Default | Effect |
|---|---|---|
| `ExportOnSave` | `All` | Every save in the backoffice writes the item's file to the working folder. |
| `ImportOnFirstBoot` | `true` | A new database imports the working folder once. |
| `FirstBootGroup` | `All` | The first import takes definitions and content. |
| `ImportAtStartup`, `ExportAtStartup` | `None` | Ordinary restarts import and export nothing. |

uSync records its first boot in the database, so restarts and updates never import on their own.

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
- **Content.** The Data sources root with the **Recently changed pages report** definition, and
  the samples: the Sample Application, the Website theme demo, and the Orders data sources,
  report definitions and lists. [Websites, pages and blocks](application-blocks.md#sample-pages)
  lists them. A host with `UmbracoAppHost:Samples` set to `false` gets the Data sources root and
  that report definition, and none of the samples.

The export assigns no domains, and its pages have English content only. `/` serves the first
root page by sort order, which is the Sample Application, so keep the sort orders when adding
root pages. Assign domains to a Web Site to publish its languages at their own addresses.

Accounts, Public Access rules, application databases and uploaded media are outside the export.
Configure Public Access in each installation.

### Definitions that belong to AppBlocks

The form, field, layout and content blocks, the Interactive Report block, and the data source,
list and report definition types come from the `Schema/` folders of AppBlocks and its companions.
They are in the **AppBlocks** folder of Document Types and Data Types. AppBlocks installs them
through Umbraco's package migrations, and the host adds two rules around uSync:

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

## First start

A working folder without `usync.config` has never been filled. Before uSync's first import, the
host fills it in this order and never replaces a file that is already there:

1. The library's export, without the sample content when `UmbracoAppHost:Samples` is `false`.
2. The project's own export, the folder `UmbracoAppHost:HostExport` names (default `uSync` in the
   project root), for every path the library does not ship.
3. The embedded `uSync/` files of each registered [theme library](themes.md#a-theme-library), for
   paths that neither of the first two ships.

`new-site.ps1` and `reset.ps1` copy the saved export into the working folder themselves, for the
reference website and for sites whose application keeps the samples.

The host also writes the views the library ships to the project's `Views/` folder before the
import, so Umbraco finds a Razor file for every template registration. At each later start it
rewrites a shipped view that differs from the library's. `_ViewImports.cshtml` is written only
when it is missing, and a project's other views are left alone.

Umbraco's installer creates English, so that its content cache can start before uSync runs. Set
`Umbraco:CMS:InstallDefaultData:Languages` to install other languages. uSync then imports the
saved language definitions, French included.

A failed first import is reported in uSync's log. Fix the exported files and
[reset the instance](getting-started.md#reset-the-reference-website) to run the first boot again.

## Save a backoffice change

1. Make the change in the reference backoffice. uSync writes the item's file to
   `reference/uSync/`.
2. Copy that file to the same relative path in `src/UmbracoAppHost/uSync/`. When you copy the
   whole folder, leave out the paths that `deployment/appblocks-definitions.txt` lists, which
   uSync also writes there. Delete a file from the saved export when its item was deleted on
   purpose, because a copy leaves old files in place.
3. Review the change and commit it with any related code or view changes.

`reset.ps1` compares the working folder with the saved export before it deletes anything. It
lists the files that were changed, added or removed in the working folder and asks before
discarding them. The comparison leaves out AppBlocks' definitions and
`ContentTypes/paperreport.config`, whose allowed templates the host rewrites at run time.

## Deliver a change to existing sites

Existing sites keep their definitions until someone imports new ones.

1. Run `update-site.ps1 -Site <folder>`. After it installs the build, it copies the saved
   export's definition folders (`ContentTypes`, `DataTypes`, `Dictionary`, `Languages`,
   `MediaTypes`, `MemberTypes`, `RelationTypes` and `Templates`) into the site's `uSync/` folder
   and lists the files it changed. It also removes the site's copies of AppBlocks' definitions
   and lists them.
2. Start the site and open **Settings → uSync** in its backoffice.
3. Run **Report** to review the changes, then **Import**.

The script leaves `Content/` in place: pages belong to the site after it is created. Copy a
content file by hand when a site should receive it. Files the site added, and files the saved
export no longer ships, also stay.

For an instance that a host runs from its own project, `refresh-definitions.ps1 -Instance
<folder>` copies the same folders from the export in the host's build output. See
[Bring an upstream change down](getting-started.md#bring-an-upstream-change-down).

AppBlocks' own definitions reach an existing site through its package migrations, which run at
startup unless `Umbraco:CMS:PackageMigration:RunSchemaAndContentMigrations` is `false`.
