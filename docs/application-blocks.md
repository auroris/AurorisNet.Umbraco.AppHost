# Websites, pages and blocks

Two document types build every site: **Web Site** for a site or a section of one, and **Web
Page** for its pages. Both hold content, forms and reports as blocks. This page describes the
types, their templates and menus, language fallback, the blocks a page offers, and the sample
pages. The blocks themselves come from
[AppBlocks](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks), whose guides describe
their rules in full.

## Web Sites and Web Pages

A Web Site (`appHostWebsite`) can be a root or sit inside another site. A Web Page (`webPage`)
belongs to its nearest Web Site and takes that site's template and theme. Neither type has
properties of its own. They compose these types, each of which adds one tab:

| Composition | Alias | Properties | Tab | Used by |
|---|---|---|---|---|
| Page Content | `webPageContent` | Title, page blocks | Content | Site and page |
| Page Sidebar | `webPageSidebar` | Sidebar content | Content | Site and page |
| Page Navigation | `pageNavigation` | Hide from navigation, spacing, group, external link | Navigation | Site and page |
| Site Menu | `navigationMenu` | Authored menu, profile page | Navigation | Site |
| Site Presentation | `sitePresentation` | Site title, badge, theme | Appearance | Site |
| Page Code | `webPageCode` | CSS, JavaScript | Code | Site and page |

A document type of your own can compose the ones it needs.

A typical tree puts a public site at the root and the application inside it:

```text
Web Site — Website home template (the public site)
  Overview, support, documentation — Web Pages
  Sign in — Web Page with the Site sign in template
  Access denied — Web Page
  Web Site — Application site template (the application)
    Application pages with forms and reports
```

### Templates

| Template | Alias | Use |
|---|---|---|
| Website home | `WebsiteHome` | On a Web Site: the website theme for the site and its pages. |
| Application site | `ApplicationSite` | On a Web Site: the application theme for the site and its pages. |
| Web page | `WebPage` | The default of a Web Page. Routing replaces it with the template of the nearest Web Site. |
| Site sign in | `SiteSignIn` | On a Web Page: the page's content followed by the member sign-in form, for a login page. |
| Signed-in page | `AppBlocks` | On a Web Page: shows the page to a signed-in visitor and the theme's sign-in layout to anyone else. |
| Report Page | `ReportPage` | The template of the [Report Page](interactive-reports.md#report-page) type. |

Choose **Website home** or **Application site** in a Web Site's template selector. Its Web Pages
keep **Web page** and follow the site. A site inside another site starts its own scope. A page
with another template selected, or an alternate template in the request, keeps it. The site's
optional **Theme** property selects a registered theme in place of the template's; see
[Themes](themes.md).

The Signed-in page template decides what the page shows. It does not protect the page's block
endpoints, so use Public Access to protect a branch.

### Menus

A page shows the menu of its nearest ancestor with an authored menu, and the badge of its
nearest ancestor with one. A site with no authored menu lists its pages. Generated menus, section
links, breadcrumb links and page cards follow **Hide from navigation** and the visitor's page
access, including inherited Public Access and the host's admission rules. Paper reports are
excluded. An authored menu whose entries are all hidden from a visitor stays empty.

Custom themes can pass their published candidates to `await pageAccess.NavigationPagesAsync(candidates)`
using the request's `AppPageAccess`. The returned pages keep the supplied order.

A menu entry's label or link can carry tokens for the person looking at the page:

| Token | Value |
|---|---|
| `{{user.name}}`, `{{user.username}}`, `{{user.email}}` | The signed-in member or backoffice user. |
| `{{user.profile}}` | The **Profile page** picked on the nearest Site Menu, for a member. |
| `{{user.backoffice}}` | The backoffice address, for a backoffice user. |
| `{{user.signout}}` | The application's sign-out. Both themes render the entry as a form button, because signing out is a POST. |

An entry whose label or link has a token with no value for the visitor is left out with its
children. "Signed in as {{user.name}}" therefore appears only to someone signed in, and a
profile entry only once a profile page is picked. A misspelt token stays visible as typed.
UmbNav's own settings, hide when logged in and hide when logged out, apply as well, and count a
backoffice user as logged in. The **Website theme demo** menu carries these entries as an
example.

### Page CSS and JavaScript

The **Code** tab's CSS and JavaScript apply to the page and to every page beneath it. They are
written from the outermost ancestor down to the page, followed by the page's CSS and JavaScript
blocks. Page content is not inherited.

### Protect a branch

Use Umbraco **Public Access** on the application's Web Site to choose the member groups, the
login page and the access-denied page. Its pages inherit the protection, and the public site
stays open. Keep the login and access-denied pages outside the protected branch. uSync does not
export Public Access rules, so set them in each installation.
[Who a protected page admits](sign-in-and-accounts.md#who-a-protected-page-admits) describes how
backoffice editors reach a protected page.

**On a page without Public Access, a C# form class decides for an anonymous visitor**, so a
class with no check of its own is public. Report blocks need an account in this host unless
[`Accounts:AllowAnonymousReportBlocks`](interactive-reports.md#anonymous-visitors) is on.
AppBlocks' [access rules](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/site-integration.md#access-and-caching)
cover data sources, attachments, tabular forms and page caching.

## Language fallback

A site shows a page in every language whose Umbraco language names a **Fall back to** language,
with the fallback language's content where a translation is missing. The saved export sets
French to fall back to English, so a French address serves the French page where one is
published and the English page where it is not. The default language is served as published.

Under a domain assigned to such a language, `CultureFallback` (namespace
`UmbracoAppHost.Website`) routes pages by these rules:

- A page published in the language keeps its own URL segment and content.
- A page published only in the fallback language is served at its fallback segment with the
  fallback content, while the domain and the interface stay in the requested language. A page
  beneath an ancestor published only in the fallback language is reachable the same way.
- When a translated sibling already holds that segment, the page takes the language code as a
  suffix: `orders` becomes `orders-en`, then `orders-en-2` if another sibling holds `orders-en`.

Navigation in the language lists the pages these routes serve. Property reads in the language
use Umbraco's language fallback by default, so a value missing in French comes from English. A
read that names its own `Fallback` keeps it. A fallback language that itself falls back
continues the chain. A page published in none of the chain's languages returns 404, and drafts
stay unpublished in every language.

The rules follow the language settings: change **Fall back to** in **Settings → Languages**, and
routes, navigation and values follow on the next request.

## Blocks on a page

The **page blocks** property of a site or page uses the **Website: Page blocks** grid, twelve
columns wide. It offers AppBlocks' layout, content, form and data blocks and the Interactive
Report block. The same blocks render under both themes.

### Layouts

| Block | Alias | Areas | Notes |
|---|---|---|---|
| Full width | `appFullWidth` | One, 12 columns | A row across the grid. |
| Two columns | `appColumns` | Two, split 6/6, 4/8 or 8/4 | Choose the split in the block's settings. With no choice the split is 6/6. |
| Three columns | `appThreeColumns` | Three, 4 + 4 + 4 | |
| Region | `appRegion` | One, 12 columns, under an optional title bar | **Collapsible** in its settings lets visitors fold it from the title bar. |

Page layouts can hold standalone blocks such as Interactive Report, rich text and attachments.
Inside a Form, use **Form full width**, **Form two columns**, **Form three columns** or **Form region**
to arrange fields. The page and Form layouts use the same column splits and collapsible-region settings.
Bound fields, fieldsets, Anti-spam and Form actions are available anywhere inside a Form.
On website pages the Form also offers **People picker**. Under **Button and form fields**, choose the writable text boxes in this form that will receive the person's SID and display name. Directory search, access, and caching and limits each have their own settings group.

A block's span inside an area counts that area's columns, so a block that fills a Three columns
area spans 4. The split changes the two areas on the page, while the backoffice draws them 6 + 6.

[Themes](themes.md#what-a-theme-styles) lists the markup hooks the layouts emit.

### Content blocks

| Block | Alias | Renders |
|---|---|---|
| Rich text | `appRichText` | Where it sits, in the theme's typography. |
| HTML | `appHtml` | As written, where it sits. Inside a form it cannot contain a form of its own, and a button in it needs `type="button"` or it submits the form. |
| CSS | `appCss` | Once in the page's `<head>`, however many blocks carry the same code. It applies to the whole page. |
| JavaScript | `appScript` | Once at the end of the page's `<body>`. |

CSS and JavaScript blocks take no cell of the grid.

**Nothing checks this code at publish time.** Back-office access is the trust boundary: anyone
who can edit a page can put HTML, CSS and JavaScript on it, and the script runs with the session
of whoever opens the page. Give the Content section only to people you would let change the
application.

**Content Security Policy.** The library sends no policy. A site that sends one with a nonce
stores the request's nonce in `HttpContext.Items["CspNonce"]` from its own middleware. The page's
styles and scripts, and AppBlocks' own, then carry that nonce.

### Fields

A field block (text box, text area, number, date, time, date and time, checkbox, select,
shuttle, color picker and display value) binds one field of its form. Besides its binding,
label and help text, each has:

| Property | Effect |
|---|---|
| Required on this form, Read only on this form | Narrow the field for this form. The form class's or data source's own validation always applies. |
| Placeholder | Shown in the empty field. |
| Hidden | A hidden input with no label and no grid cell, bound and submitted like any field: for keys and foreign keys. The browser still sends it, so a value that must be trusted belongs to the form class or data source. |
| Default value | Fills the field when a new record leaves it empty: text, or one bind variable such as `:USER_NAME`, `:NOW` or `:PARAM.name`. |
| Width | The columns of the grid the field spans, from 1 to 12, capped at the grid the field sits in. |

AppBlocks' [business rules guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/business-rules.md)
lists the bind variables and describes validation, concurrency and duplicate submissions.

## Forms

1. Add a **Form** block to a page. In its **Form Interface**, select a registered C# form class
   or a published SQL data source, the query parameter that carries the record's key (`id` by
   default) and a return page.
2. Add field blocks to the form, arranged with layouts and fieldsets, and a **Form actions**
   block for the buttons.
3. To open the form from a report, select the form page on the report block and name the
   report's key column, such as `ORDER_ID`. The report's create link opens the page with no key,
   and its row link opens `orders-form?id=4`.
4. Publish. Publishing checks the bindings against the class or data source.

A project registers a C# form class with dependency injection, from its `Program` class:

```csharp
public partial class Program : IUmbracoAppHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
        => builder.Services.AddScoped<IFormResource, MyFormResource>();
}
```

**Every operation of a form class must authorize the caller and the record itself.** The
capabilities it returns decide which buttons show and grant nothing. The working example is
`src/UmbracoAppHost/Sample/OrdersFormResource.cs`, which accepts signed-in members and backoffice
users in Administrators, compares a version column on update and delete, and makes a repeated
create return the first result. AppBlocks' business rules guide covers each of these.

## Data sources, lists and report definitions

A **Data sources** root holds the published nodes that forms, reports, lists, tabular forms and
schedules read:

- **SQL data source**: a table or a SELECT on one connection, with its key, allowed operations,
  access and row restriction.
- **Report definition**: a report over a data source, selectable wherever a report name is.
- **List of values**: the choices of a select field.

Being content, they are versioned, published, exported by uSync and guarded by Umbraco's
permissions. Their connections are the ones [Databases](databases.md) lists: `ReportsSampleDb`
with the samples on, `AppBlocksDb`, `UmbracoDb`, and those a project registers.

To add a data source, create a **SQL data source** node beneath **Data sources**, fill in its
alias, connection, row source and key columns, and choose its **Access** and allowed
operations. The editor's **Test** action runs the row source of the draft, unsaved edits
included, with sample bind values entered as JSON, such as `{"PARAM.id":"4"}`. It shows the
fields with their columns and kinds and one matching row, and saves nothing. Publishing checks
the definition against the database and refuses it with the reasons when something does not
fit.

A few things are specific to this host:

- The member groups in `Accounts:EnsureMemberGroups` are created before every uSync import, so
  the read and write groups an exported data source names exist on a new site.
- On a first import, uSync publishes the nodes of one level in sort order, and publishing a
  page checks the sources it uses. Give source nodes a lower sort order than the pages that
  use them, as the samples do.
- SQLite and SQL Server drivers come with Umbraco. A project whose data sources read PostgreSQL
  or Oracle references `Npgsql` or `Oracle.ManagedDataAccess.Core` itself.

AppBlocks' [business rules guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/business-rules.md)
describes access, row restrictions, audit columns, custom statements, backing classes and
`IDataSourcePolicy`, and its [Connections guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/connections.md)
the databases a data source can use.

## Attachments, tabular forms and schedules

| Block | Alias | What it does | Guide |
|---|---|---|---|
| Attachments | `appAttachments` | Keeps files with a record. Inside a form it follows the form's record; on its own it names the record with **Owner resource** and **Owner key**. | [Business rules](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/business-rules.md) |
| Tabular form | `appTabular` | Edits the rows of a child data source that belong to one record, such as the lines of an order, saved together. | [Business rules](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/business-rules.md) |
| Schedule | `appSchedule` | Shows the rows of one source against time, with the bars of another. | [Schedule block](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/schedule.md) |

An Attachments block's **Purposes**, one to a line, file each upload under a label such as
Invoice. **Largest file (MB)** and **Accepted types** narrow the site's limits for the block,
and **Read only** shows and downloads the files and refuses changes. In this host the files go
to the `AttachmentsDb` connection, never to Umbraco media, so only someone who may read the
record can read them. [Databases](databases.md#record-attachments) has the site's limits and
the purge of removed files.

A Tabular form names its **Data source**, the **Link field** that holds the record's key, and
the **Columns** to show. **Without adding**, **Without editing**, **Without deleting** and
**Read only** narrow what the data source allows for the block. **Row limit** is the most rows
it lists for one record: 200 unless set, and at most 1000.

The **Anti-spam** block protects a public form; see AppBlocks'
[Anti-spam guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/anti-spam.md).

## Paper reports

A **Paper report** node prints the rows of a SQL data source through a Razor template, as a PDF
from a Pressroom server or as self-contained HTML. AppBlocks' Documents companion supplies the
engine; its [README](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/src/AppBlocks.Documents/README.md)
covers the Pressroom settings in **Settings → AppBlocks → Documents**, access and asset
embedding.

| Property | Value |
|---|---|
| Data source | A published SQL data source. |
| Parameters | JSON that maps the data source's parameters to literals or bind variables, such as `{"id":":PARAM.id"}`. Empty forwards the request's query parameters. |
| Page options | Pressroom's render options as JSON, such as `pageSize`, `landscape`, `margins`, `headerTemplate` and `footerTemplate`. |
| File name | A pattern such as `order-{id}-{date:yyyy-MM-dd}.pdf`. `{date}` is today's date and any other name in braces is a parameter. Empty gives `report-{date}.pdf`. |
| Culture | A culture such as `fr-CA`, or a bind variable such as `:PARAM.culture`. Empty uses the culture of the request. Publishing refuses a literal that is not a culture. |
| Maximum rows | Empty means 5000, and the largest value is 50000. |

The node's template is its print layout and receives `PaperReportModel`: the rows and columns,
the parameters, the signed-in person, the culture and the generation time. To add a template,
add its registration to the project's uSync export under `Templates/` and its view to `Views/`.
The document takes the print stylesheet of the nearest site's [theme](themes.md).

`/api/paper-reports/{node key}` serves the document to a signed-in member or backoffice user
whom the node's Public Access and the data source admit. `?format=html` asks for HTML when a
Pressroom server is configured. Select the node in a Form or report block's **Paper report**
property to add a Print action that passes the record's key.

## Sample pages

With `UmbracoAppHost:Samples` on, the saved export adds these nodes. All of them use the sample
orders database.

| Node | Shows |
|---|---|
| **Sample Application** | A Web Site with the Application site template. |
| About | A plain Web Page. |
| Orders | A Report Page for the `orders` report. |
| Orders form | A form on the C# class `sample.orders`, with region and status lists, an Attachments block and an **Order lines** tabular form over `orders.lines`. Open `?id=4` to load an order. |
| Orders interactive | An Interactive Report block over the `ORDERS` table. |
| Layouts | Every layout, region and fieldset, in a read-only form. |
| Content blocks | Rich text, HTML, CSS and JavaScript blocks, and a form with a default value, a hidden field and field widths. |
| Orders schedule | A Schedule block: customers as rows and orders as bars. |
| Orders paper report, Commandes rapport papier | English and French paper reports with the `OrdersPaperReport` template. |
| **Website theme demo** | A Web Site with the Website home template and an authored menu with visitor tokens. |
| Website layouts, Website content blocks, Website orders schedule | The same blocks under the website theme. |
| Website authoring | A public page with inherited navigation, two-column content, and page CSS and JavaScript. |
| **Data sources** | `orders`, `orders.lines`, `paper.orders`, `orders.schedule-rows` and `orders.schedule-bars`, all on `ReportsSampleDb` with **Not public** access; the report definitions `orders` and `orders-schedule`; and the lists `order-region` and `order-status`. `orders` and `orders.lines` use **Checksum** concurrency, so a save of a row that changed since it was loaded is refused. |

The Data sources root also holds the report definition `umbraco-recent-pages`, which every site
gets.
