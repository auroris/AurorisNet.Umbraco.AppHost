using System.Data.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Runtime;
using Umbraco.Cms.Infrastructure.Runtime.RuntimeModeValidators;
using CmsEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace UmbracoAppHost.Hosting;

internal sealed class UmbracoStartupCheck(CmsEnvironment cms, IWebHostEnvironment web, ILoggingConfiguration logs,
    IOptions<RuntimeSettings> runtime, RuntimeModeValidatorCollection validators, IDbProviderFactoryCreator providers,
    IOptionsMonitor<ConnectionStrings> database, ILogger<UmbracoStartupCheck> logger) : IAppHostStartupCheck
{
    public async Task CheckAsync(AppHostStartupReport report, CancellationToken cancellationToken)
    {
        CheckFolder(() => cms.LocalTempPath, "Umbraco’s local cache folder (normally app/umbraco/Data/TEMP, selected by Umbraco:CMS:Hosting:LocalTempStorageLocation)", report);
        CheckFolder(() => cms.TemporaryFileUploadPath, "Umbraco’s temporary upload folder (normally app/umbraco/Data/TEMP/TemporaryFile, set by Umbraco:CMS:Hosting:TemporaryFileUploadLocation)", report);
        CheckFolder(() => logs.LogDirectory, "Umbraco’s log folder (normally app/umbraco/Logs, set by Umbraco:CMS:Logging:Directory)", report);
        CheckFolder(() => Path.Combine(web.ContentRootPath, "Views"), "the application's Views folder", report);

        foreach (var validator in validators)
        {
            if (validator.Validate(runtime.Value.Mode, out var detail)) continue;
            logger.LogError("Umbraco runtime validation: {Detail}", detail);
            var remedy = validator switch
            {
                UseHttpsValidator => "Configure an HTTPS binding for the site and set Umbraco:CMS:Global:UseHttps to true in instance.json.",
                UmbracoApplicationUrlValidator => "Set Umbraco:CMS:WebRouting:UmbracoApplicationUrl in instance.json to the public HTTPS address, including the application path.",
                ModelsBuilderModeValidator => "Set Umbraco:CMS:ModelsBuilder:ModelsMode to Nothing in instance.json.",
                JITOptimizerValidator => "Publish the application in Release configuration and deploy that build.",
                _ => "See the host log for the runtime validation error and correct the setting in instance.json.",
            };
            report.AddProblem("Umbraco’s configured runtime mode requires a change.", remedy);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var settings = database.CurrentValue;
            if (!settings.IsConnectionStringConfigured()) return; // interactive installer
            await using var connection = providers.CreateFactory(settings.ProviderName)?.CreateConnection();
            if (connection is null)
            {
                report.AddProblem("Umbraco could not load the configured database provider.",
                    "In instance.json, check ConnectionStrings:umbracoDbDSN_ProviderName. For SQL Server use Microsoft.Data.SqlClient. For SQLite use Microsoft.Data.Sqlite. Include the matching provider in the application.");
                return;
            }
            connection.ConnectionString = settings.ConnectionString;
            // Umbraco wraps provider connections and opens SQLite in ReadWrite mode. Its installer creates the file separately.
            var sqlite = string.Equals(settings.ProviderName, Umbraco.Cms.Persistence.Sqlite.Constants.ProviderName, StringComparison.OrdinalIgnoreCase);
            if (sqlite && !CheckSqliteFile(connection, report)) return;
            await connection.OpenAsync(timeout.Token);
            if (sqlite)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_schema";
                await command.ExecuteScalarAsync(timeout.Token);
            }
            else if (string.Equals(settings.ProviderName, Umbraco.Cms.Persistence.SqlServer.Constants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT CASE WHEN OBJECT_ID(N'umbracoNode', N'U') IS NULL
                        AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 0 THEN 1 ELSE 0 END
                    """;
                if (await command.ExecuteScalarAsync(timeout.Token) is 1)
                    report.AddProblem("Umbraco cannot install its SQL Server tables.",
                        "Ask the database administrator to verify that the site account can see the CMS database and create its installation tables there.");
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(error, "Umbraco database startup check failed");
            report.AddProblem("Umbraco could not open its CMS database.",
                "Check umbracoDbDSN and umbracoDbDSN_ProviderName in the ConnectionStrings section of instance.json. For SQL Server, verify the database name, TLS trust and the site account’s access. For SQLite, verify the file and folder permissions.");
        }
    }

    // Probe existing files without SQLite's create-on-open behavior. A fresh install needs only its writable directory.
    private static bool CheckSqliteFile(DbConnection sqlite, AppHostStartupReport report)
    {
        var settings = new SqliteConnectionStringBuilder(sqlite.ConnectionString);
        var path = settings.DataSource;
        string? uriMode = null;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('?', 2);
            var query = QueryHelpers.ParseQuery(parts.Length == 2 ? parts[1] : "");
            uriMode = query.TryGetValue("mode", out var mode) ? mode.ToString() : null;
            path = Uri.TryCreate(parts[0], UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath : Uri.UnescapeDataString(parts[0][5..]);
            if (uriMode == "rwc")
            {
                query["mode"] = "rw";
                settings.DataSource = QueryHelpers.AddQueryString(parts[0], query);
            }
        }
        if (settings.Mode == SqliteOpenMode.Memory || uriMode == "memory" || path is ":memory:" or "") return false;
        if (settings.Mode == SqliteOpenMode.ReadOnly || uriMode == "ro")
        {
            report.AddProblem("Umbraco’s SQLite database is configured as read-only.",
                "In instance.json, set ConnectionStrings:umbracoDbDSN to a writable SQLite database so Umbraco can install and save changes.");
            return false;
        }
        var file = Path.GetFullPath(path);
        CheckFolder(() => Path.GetDirectoryName(file)!, "the Umbraco SQLite database folder", report);
        if (uriMode is null or "rwc")
        {
            try { File.GetAttributes(file); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        settings.Mode = SqliteOpenMode.ReadWrite;
        settings.Pooling = false;
        sqlite.ConnectionString = settings.ToString();
        return true;
    }

    private static void CheckFolder(Func<string> resolve, string area, AppHostStartupReport report)
    {
        try { InstanceConfiguration.CheckWritableDirectory(resolve(), area, report); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Record(StartupFailure.Storage(area, "configured by Umbraco", error));
        }
    }
}
