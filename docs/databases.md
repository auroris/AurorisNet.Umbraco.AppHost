# Databases

An instance keeps its databases in its own folder, outside the application, as SQLite files
unless a connection string says otherwise. This page lists those files, shows how a project adds
a database of its own, and gives the steps to move Umbraco to SQL Server.

## The files of an instance

All paths are relative to the instance folder: `reference/` for the reference website, the site
folder for a site that `new-site.ps1` created.

| File | Holds | Connection | To use another database |
|---|---|---|---|
| `data/Umbraco.sqlite.db` | Umbraco: content, users and members | `UmbracoDb` for reports and data sources | Set `ConnectionStrings:umbracoDbDSN` and `umbracoDbDSN_ProviderName`. |
| `data/AppBlocks.sqlite.db` | AppBlocks' default application database | `AppBlocksDb` | Set `ConnectionStrings:AppBlocksDb` and `AppBlocksDb_ProviderName`. |
| `data/Attachments.sqlite.db` | Record attachments: the files and their links to records | `AttachmentsDb` | Set `Attachments:Connection` to another connection. |
| `data/ReportsSample.sqlite.db` | The sample orders, when `UmbracoAppHost:Samples` is `true` | `ReportsSampleDb` | Set `ConnectionStrings:ReportsSampleDb` and `ReportsSampleDb_ProviderName`. |

Each application database is created the first time something opens it. AppBlocks places these
files beside Umbraco's SQLite file. When Umbraco's database is not a SQLite file, they go to
`data/App_Data/`. A file that already exists in `data/App_Data/` and not beside Umbraco's keeps
being used there, so an upgrade never starts an empty database; move it while the site is
stopped to change that.

The same `data/` folder holds the Data Protection keys in `data/keys/`, the embedded identity
server's keys in `data/keys/identity-server/`, and AppBlocks' saved connections and document
settings in `data/App_Data/AppBlocks/`. Back up the whole instance folder: connection strings
saved in the backoffice can be read only with the Data Protection keys beside them.

A connection string without a provider name is read as SQL Server, as Umbraco reads one. Put
connection strings and secrets for a server in that instance's `instance.json`, or in
environment variables.

**Settings → AppBlocks → Connections** lists the connections and lets an administrator add
more. AppBlocks' [Connections guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/connections.md)
covers that screen, the database drivers a site installs for PostgreSQL and Oracle, and the
upload store.

### Record attachments

The library's defaults for the `Attachments` section are:

| `Attachments` setting | This host's default | Effect |
|---|---|---|
| `Connection` | `AttachmentsDb` | The connection whose database holds the files. AppBlocks' own default is `AppBlocksDb`. |
| `MaxFileSize` | `26214400` (25 MB) | The largest file any Attachments block accepts, in bytes. |
| `AllowedExtensions` | empty | Extensions such as `.pdf` that every block is limited to. Empty allows any. |
| `RetentionDays` | `30` | How long a removed file is kept before a purge deletes it. |

A project with a database of its own can name that connection in `Attachments:Connection`, so
records and their files share one database and one backup.

A removed file leaves the lists at once and stays in the database. To delete removed files,
send `POST /umbraco/management/api/v1/attachments/purge` with the backoffice token of an
administrator. The optional body `{ "retentionDays": 30 }` replaces the configured retention.
The answer gives the number of links, files and bytes deleted.

### Saved report views

The library's defaults set `InteractiveReport:SavedReports:connection` to `ReportsDb`. Neither
this library nor AppBlocks registers a connection of that name, so define it for the site, as
`ConnectionStrings:ReportsDb` with `ReportsDb_ProviderName` or as a saved connection in
**Settings → AppBlocks → Connections**, or set the setting to another connection such as
`AppBlocksDb`. **Settings → AppBlocks → Database setup** shows whether the store can be used.
While it cannot, reports still load, query and export, and saving views is not offered.

## A database of your own

A project with tables of its own registers its database from the connections callback, with a
folder of schema scripts:

```csharp
using AppBlocks.Connections;
using AppBlocks.Databases;
using UmbracoAppHost;

var builder = WebApplication.CreateBuilder(args);
builder.AddUmbracoAppHost<Program>();

await using var app = builder.Build();
await app.UseUmbracoAppHostAsync<Program>();
await app.RunAsync();

public partial class Program : IUmbracoAppHost
{
    public static void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections)
        => connections.AddApplicationDatabase("MyHostDb",
            ApplicationSchema.FromDirectory(Path.Combine(builder.ProjectRoot(), "Schema")));
}
```

- The connection is registered with AppBlocks under its name. Forms, data sources and reports
  use it like any other, and **Settings → AppBlocks → Connections** lists it as read-only.
- Its connection string is `ConnectionStrings:MyHostDb` with `ConnectionStrings:MyHostDb_ProviderName`.
  With no connection string, the database is the SQLite file `MyHostDb.sqlite.db` in the
  instance's `data/` folder. A third argument to `AddApplicationDatabase` names another file.
- The schema has one folder for each database type, such as `Schema/sqlite/` and
  `Schema/sqlserver/`, whose `.sql` files run in file-name order the first time the process
  opens the connection. Write them so they are safe to run again, for example with
  `CREATE TABLE IF NOT EXISTS`. `ApplicationSchema.FromEmbedded(assembly, "Schema")` reads the
  same layout from embedded resources.
- `builder.ProjectRoot()` is the project folder: the content root, unless
  `UmbracoAppHost:ProjectRoot` names another folder, as a test site does.

The scripts are not content files. Add this to an `ItemGroup` of the project file so a published
site carries them:

```xml
<None Update="Schema\**\*.sql" CopyToPublishDirectory="PreserveNewest" />
```

AppBlocks' [Connections guide](https://github.com/auroris/AurorisNet.Umbraco.AppBlocks/blob/main/docs/connections.md#application-databases)
describes the version table, run-once scripts, C# steps and what happens when a script fails.

## The sample database

With the samples on, the host registers `ReportsSampleDb` as an application database. Its schema
is embedded in the library from `src/UmbracoAppHost/Sample/Schema/`, with scripts for SQLite and
SQL Server: the `ORDERS` and `ORDER_LINES` tables, the `ORDERS_SCHEDULE_BARS` view and a trigger
that advances each row's `VERSION`. A seed adds 500 orders, and one to three lines for each of
the first 120, when the tables are empty.

The `orders` report, the Orders data sources and schedule, and the `sample.orders` form class
all use this connection, so `ConnectionStrings:ReportsSampleDb` moves the whole sample to SQL
Server. The sample has no scripts for PostgreSQL or Oracle.

## Move Umbraco to SQL Server

Treat a change of database provider as a migration of its own, with a backup and a rehearsal.
uSync carries definitions and content to the new database through the instance's `uSync/`
folder; accounts and saved reports move separately.

1. Create an empty SQL Server database and grant the application's login `db_owner`.
2. Set `ConnectionStrings:umbracoDbDSN` to it, with `umbracoDbDSN_ProviderName` set to
   `Microsoft.Data.SqlClient`, and keep `Umbraco:CMS:Unattended:InstallUnattended` at `true`.
3. Start the site. The unattended install creates the schema and the administrator, and uSync's
   first boot imports the instance's `uSync/` folder. Check the import, and import any further
   exports the site needs from **Settings → uSync**.
4. Keep the instance's `media/` folder with the site.
5. Move members. uSync does not export them. With the default settings a member is created
   again at the person's first sign-in; migrate group assignments and history when the site
   needs them.
6. Move saved reports. On the old site, open the report administration page at `/api/reports/admin` and **Download**
   each report's JSON. On the new site, use **Upload JSON…**, then publish as needed.

The application databases are separate files and stay where they are, unless their connection
strings move them too.
