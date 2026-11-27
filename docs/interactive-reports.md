# Interactive Reports

Interactive Reports reach a site through AppBlocks' Interactive Reports companion, which
supplies the report block, the endpoints, saved views and the report engine. This page describes
what the host changes: who administers and reads reports, the built-in reports, and the Report
Page type. The companion's
[README](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/src/AppBlocks.InteractiveReports/README.md)
covers report blocks, printing and the saved-view store.

## What the host sets

| Setting | This host's default | Effect |
|---|---|---|
| `AppBlocks:IncludeBackOfficeReports` | `true` | Adds the three [built-in reports](#built-in-reports). |
| `InteractiveReport:WhoamiEnabled` | `true` | Turns on the report engine's `whoami` endpoint, which answers who the engine sees as the caller. |
| `InteractiveReport:SavedReports:connection` | `ReportsDb` | The connection that stores saved views. See [Databases](databases.md#saved-report-views). |
| `Accounts:ReportAdministratorUserGroups` | `admin`, `interactiveReportAdministrators` | Backoffice groups whose users administer reports. |
| `Accounts:AllowAnonymousReportBlocks` | `false` | Whether a visitor without an account may read report blocks. |

The companion maps the website endpoints under `/api/reports` and `/api/download`, and the
backoffice endpoints under `/umbraco/reports` and `/umbraco/report-download`. The website
endpoints accept the member session and the backoffice session. The backoffice endpoints accept
the backoffice session alone.

## Who may do what

`Reports/ReportAccessPolicy.cs` replaces the companion's access policy.

A caller is a **report administrator** when either holds:

- They are an approved backoffice user who is not locked out, in Umbraco's `admin` group, in
  AppBlocks' `interactiveReportsAdministrator` group, or in a group that
  `Accounts:ReportAdministratorUserGroups` or `AppBlocks:ReportAdministratorGroups` lists. The
  host creates the group **Interactive Report Administrators**
  (`interactiveReportAdministrators`) at startup, with access to the Content section.
- They are a member whose sign-in is linked to such a backoffice user. The host follows the
  member's external login key, the Windows SID, to a backoffice user linked under the same key.
  Email addresses are never matched without a verified external login link.

| Action | Allowed for |
|---|---|
| View, query and export a report, list its saved views, save a view | A report administrator. Other website members or backoffice report users can open reports available to their account on the corresponding routes. |
| Read a saved view | A report administrator. Its owner, and everyone signed in when the view is public or the default, while they may read the report itself. |
| Change or delete a saved view | A report administrator. Its owner, while they may read the report itself. |
| Choose the default view of a report block | Nobody: a published block's own settings are its starting view. |
| Every other action, such as the administration pages | A report administrator. |

A report is for administrators only when its definition names the authorization policy
`UmbracoReportAdministrators`, as two of the built-in reports do, or when it is one of the
engine's own listings, whose names start with `__`.

A saved view belongs to the member when a member is signed in, and otherwise to the backoffice
user.

### Anonymous visitors

With `Accounts:AllowAnonymousReportBlocks` at its default of `false`, every report needs an
account. Set to `true`, report blocks follow AppBlocks' own rule: on a page that Umbraco Public
Access leaves open, a visitor may view, query and export a report block whose SQL binds no
`:USER_*` value. Saved views and every other action still need an account, and so does every
report that is not a block: configured reports, report definition nodes and a Report Page.

**Protect the pages that need it before turning the setting on.** The saved export carries no
Public Access rules, so on a new site every such block, the sample orders included, would be
readable by anyone.

## Built-in reports

The three definitions come from the companion and read Umbraco's database through the `UmbracoDb`
connection, on SQLite and SQL Server. A definition of the same name under
`InteractiveReport:Reports` in a site's settings replaces the built-in one.

| Report | Contents | Readers |
|---|---|---|
| `umbraco-recent-pages` | The latest saved version of every document outside the recycle bin, with its content path, last editor and save time. It supplies the [Welcome dashboard](backoffice.md#welcome-dashboard). | Backoffice users with access to Content, limited to their start nodes and Browse permissions. |
| `umbraco-members` | The site's members, with login, email, approval and last login. | Report administrators. |
| `umbraco-media` | Every media item except folders, with its folder path, type, file, size, dimensions, who created and last saved it, and whether it is in the recycle bin. The row's edit link opens the item in the media section. | Report administrators. |

## Configured reports and report nodes

A site defines reports in its settings under `InteractiveReport:Reports`, or as published
**Report definition** nodes under the Data sources root, which replace a configured report of
the same alias. With the samples on, the host registers a report named `orders` over the sample
database unless the site's settings already define one of that name.

**A named report is readable outside the page that shows it.** Every signed-in member and
backoffice user can read a configured report or a report definition through
`/api/reports/<name>`, unless the report is for administrators only. Public Access on a page
protects the report blocks on that page, and does not protect a named report that a block or a
Report Page selects. To limit a named report, give its definition the
`UmbracoReportAdministrators` authorization policy, or base it on a data source whose read
groups and row restriction decide who sees which rows.

A published **Report definition** node whose **Access** is set to **Specific groups** admits only
members of the picked member groups (and report administrators). Data source read groups and row
restrictions continue to apply on top.

A project registers the connections its reports read from the connections callback of
`AddUmbracoAppHost`; see [Databases](databases.md#a-database-of-your-own). To change the report
engine itself, for example with a row restriction callback, call the companion from a composer:

```csharp
builder.AddAppBlocksInteractiveReports(reports => reports.UseRowRestrictions(...));
```

## Report Page

The **Report Page** document type (`reportPage`) shows one report on a page of its own. Set its
**Report name** property (`reportName`) to the report's name. The page uses the nearest site's theme. A signed-in
member or backoffice user sees the report, and anyone else gets the theme's sign-in layout.
Public Access on the page applies before the view runs, as for any page. The sample **Orders**
page under Sample Application is a Report Page for `orders`.

To place a report among other content, use the Interactive Report block on a Web Page.
