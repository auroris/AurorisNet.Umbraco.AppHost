# Design: themes, blocks and data sources

This page explains the decisions behind the content model and names the repository that owns
each part, so a change goes to the right place. The guides linked below describe how each part
is used.

## The model

Editors build application pages in the CMS from blocks, and the data behind those pages comes
from definitions that are content too. The model follows Oracle APEX, where the applications
come from: regions laid out on a grid, fields bound to a row source, automatic row fetch and row
processing, lists of values, and interactive reports.

## Decisions

- **Use what Umbraco provides.** Pages are documents with a Block Grid, definitions travel
  through uSync, and Public Access protects pages. The host adds no persistence framework and no
  schema migration runner of its own.
- **Back-office access is the trust boundary.** Anyone who can edit a page may put HTML, CSS,
  JavaScript or SQL on it, and nothing checks that code further. Database credentials stay in
  configuration, never in content, and every query binds its values as parameters. A SQL data
  source can do only what its connection's database account may.
- **Definitions live in the CMS.** Data sources, report definitions and lists of values are
  nodes under one Data sources root, so they are versioned, published, exported and guarded by
  Umbraco's permissions like any content. Reports defined in `appsettings.json` remain
  supported, and a published node of the same alias replaces one.
- **Twelve columns everywhere.** Layout blocks are element types with areas on a twelve-column
  grid, and Umbraco's own grid stylesheet does the column arithmetic.
- **One markup, several themes.** Block partials emit neutral markup with stable class hooks,
  and each theme styles the hooks. A block renders under every theme, and a site chooses its
  theme through its template or its **Theme** property.
- **A paper report is a content node with its own Razor template.** A Pressroom server renders
  it to PDF. With no server configured, or when the server fails, the same self-contained HTML
  is delivered for printing from a browser.
- **SQLite first.** A new instance needs no database server. Data sources also run on SQL
  Server, PostgreSQL and Oracle.

## Who owns what

| Part | Repository | Guide |
|---|---|---|
| Web Site, Web Page and their compositions, templates, menus, language fallback, the saved export | This repository | [Websites, pages and blocks](application-blocks.md), [uSync exports](usync-exports.md) |
| The two themes and their frontend build | This repository | [Themes](themes.md), [Frontend](../Frontend/README.md) |
| Sample content, the `sample.orders` form class and the sample database | This repository | [Websites, pages and blocks](application-blocks.md#sample-pages) |
| Who administers reports, and who a protected page admits | This repository | [Interactive Reports](interactive-reports.md), [Sign-in and accounts](sign-in-and-accounts.md) |
| Form, field, layout and content blocks, data sources, attachments, tabular forms, schedules, connections | [AppBlocks](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks) | Its README and `docs/` |
| The Interactive Report block, report definitions, report endpoints | AppBlocks' Interactive Reports companion | Its README |
| Paper reports and Pressroom output | AppBlocks' Documents companion | Its README |
| The report engine | [Interactive Reports](https://github.com/auroris/AurorisNet.InteractiveReports) | Its README |
| Sign-in, the embedded identity server and account policy | [`AurorisNet.Umbraco.IdentityServer.SSO`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO) | Its README |
| Add from directory | [`AurorisNet.Umbraco.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory) | Its README |
| Administrator recovery | [`AurorisNet.Umbraco.BreakGlass`](https://github.com/auroris/AurorisNet.Umbraco.BreakGlass) | Its README |
| Hosting under a sub-path | [`AurorisNet.Umbraco.ApplicationVirtualPathFix`](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix) | Its README |
| Telemetry | [`AurorisNet.Umbraco.DisableTelemetry`](https://github.com/auroris/AurorisNet.Umbraco.DisableTelemetry) | Its README |

AppBlocks' element types and data types are in its `Schema/` folders, and AppBlocks installs
them. The saved export in this repository holds the page types and grids that use them.
[uSync exports](usync-exports.md#definitions-that-belong-to-appblocks) describes how the two are
kept apart.
