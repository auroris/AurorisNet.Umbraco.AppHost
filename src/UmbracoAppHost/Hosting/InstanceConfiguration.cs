using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace UmbracoAppHost.Hosting;

/// <summary>The data and identity owned by one host, held exclusively until that host stops.</summary>
public sealed class AppHostInstance(string root, string settingsPath, FileStream lease) : IDisposable
{
    public string Root { get; } = root;
    public string SettingsPath { get; } = settingsPath;
    public void Dispose() => lease.Dispose();
}

internal static class InstanceConfiguration
{
    public static AppHostInstance Apply(WebApplicationBuilder builder, AppHostStartupReport? report = null,
        Action<WebApplicationBuilder, AppHostStartupReport>? checkNewInstallation = null)
    {
        report ??= new AppHostStartupReport();
        using var defaultsStream = typeof(InstanceConfiguration).Assembly.GetManifestResourceStream("UmbracoAppHost.Hosting.defaults.json")!;
        var defaults = new ConfigurationBuilder().AddJsonStream(defaultsStream).Build();
        using var defaultsLifetime = defaults as IDisposable;
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = defaults.AsEnumerable().ToArray() });

        var root = Path.GetFullPath(builder.Configuration["InstanceRoot"] ?? Path.Combine("App_Data", "UmbracoAppHost"),
            builder.Environment.ContentRootPath);
        var state = Path.Combine(root, ".apphost");
        var settingsPath = Path.Combine(root, "instance.json");
        // new-site.ps1 ships empty data and media directories. App_Data is created on the worker's first run.
        var newInstallation = checkNewInstallation is not null && IsMissing(Path.Combine(root, "data", "App_Data"));
        FileStream lease;
        try { lease = AcquireLease(state); }
        catch (StartupFailure failure) when (newInstallation)
        {
            // A freshly unpacked site may have fully readable configuration and no write access.
            // Inspect its settings without creating anything or taking another process's lease.
            if (!IsMissing(settingsPath)) LoadSettings(builder, root, settingsPath, create: false);
            checkNewInstallation!(builder, report!);
            if (failure.InnerException is UnauthorizedAccessException)
            {
                if (IsMissing(settingsPath)) CheckWritableDirectory(root, "the instance folder where AppHost creates instance.json", report!);
                foreach (var name in new[] { "data", "media", "uSync" })
                    CheckWritableDirectory(Path.Combine(root, name), $"the {name} folder", report!);
                CheckWritableDirectory(Path.Combine(builder.Environment.ContentRootPath, "Views"), "the application's Views folder", report!);
            }
            throw;
        }

        try
        {
            if (Directory.Exists(Path.Combine(state, "pending-update")))
            {
                var problem = new AppHostStartupProblem("An application update was interrupted.",
                    "Stop the site, run update-site.ps1 again for this instance, then start the site.");
                throw new StartupFailure(problem, $"{problem.Description} {problem.Remedy} Instance: '{root}'.");
            }
            var needsSettings = newInstallation && IsMissing(settingsPath);
            if (needsSettings) checkNewInstallation!(builder, report!);
            LoadSettings(builder, root, settingsPath);
            if (newInstallation && !needsSettings) checkNewInstallation!(builder, report!);
            var instance = new AppHostInstance(root, settingsPath, lease);
            SiteHosting.ApplyVirtualPath(builder.Configuration);
            var data = Path.Combine(root, "data");
            var media = Path.Combine(root, "media");
            var working = Path.Combine(root, "uSync");
            var keys = Path.Combine(data, "keys");
            foreach (var path in new[] { data, media, Path.Combine(data, "App_Data"), working, keys })
                CheckWritableDirectory(path, $"the {Path.GetFileName(path)} folder", report);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAppHost:DataRoot"] = data,
                // AppBlocks keeps its files here too. A host that configured its own folder keeps it.
                ["AppBlocks:DataRoot"] = builder.Configuration["AppBlocks:DataRoot"] ?? data,
                ["Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath"] = media,
                ["uSync:Settings:RootFolder"] = working,
            });
            var database = Path.Combine(data, "Umbraco.sqlite.db");
            var managedDatabase = string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("umbracoDbDSN"));
            if (managedDatabase)
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:umbracoDbDSN"] = new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, Pooling = true }.ToString(),
                    ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
                });
            File.WriteAllText(Path.Combine(state, "runtime.json"), JsonSerializer.Serialize(new
            {
                projectRoot = builder.ProjectRoot(), managedDatabase,
                uSyncRoot = Path.Combine(AppContext.BaseDirectory, "uSync"),
                hostExport = USyncFiles.HostExport(builder.Configuration, builder.Environment.ContentRootPath, Path.Combine(root, "uSync")),
            }));
            var dp = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys))
                .SetApplicationName(builder.Configuration["Umbraco:CMS:Global:Id"]!);
            if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi();
            // A factory makes the DI container own the lease and dispose it with the host.
            builder.Services.AddSingleton(_ => instance);
            return instance;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lease.Dispose();
            throw StartupFailure.Storage("the instance state folder (.apphost)", state, error);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    // File.Exists also returns false for access denied. An unreadable instance may have valid settings.
    private static bool IsMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static FileStream AcquireLease(string state)
    {
        try
        {
            Directory.CreateDirectory(state);
            return File.Open(Path.Combine(state, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
            || (!OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 11 or 35))
        {
            var problem = new AppHostStartupProblem("Another process holds the site’s instance lock.",
                "Let the running site, reset or update finish before starting this instance again.");
            throw new StartupFailure(problem, $"{problem.Description} Instance state: '{state}'.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw StartupFailure.Storage("the instance state folder (.apphost)", state, error);
        }
    }

    internal static void CheckWritableDirectory(string path, string area, AppHostStartupReport report)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, Path.GetRandomFileName());
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose))
                stream.WriteByte(1);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Record(StartupFailure.Storage(area, path, error));
        }
    }

    private static void LoadSettings(WebApplicationBuilder builder, string root, string settingsPath, bool create = true)
    {
        try
        {
            if (create && IsMissing(settingsPath)) CreateSettings(root, settingsPath);
            // The host's appsettings, environment variables and command line retain precedence.
            builder.Configuration.Sources.Insert(1, new JsonConfigurationSource
            {
                Path = "instance.json",
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root),
                Optional = false,
                ReloadOnChange = false,
            });
        }
        catch (InvalidDataException error)
        {
            throw new StartupFailure(new("The instance settings could not be loaded.",
                "Check instance.json for invalid JSON or duplicate settings, then restart the site."), settingsPath, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new StartupFailure(new("AppHost could not read or initialize instance.json.",
                "Find the instance folder beside app. Give the site account Read access to an existing instance.json. For a fresh installation, allow the site account to create that file in the instance folder."), settingsPath, error);
        }
    }

    private static void CreateSettings(string root, string settingsPath)
    {
        var tempSettings = Path.Combine(root, Path.GetRandomFileName());
        try
        {
            using (var settingsFile = new FileStream(tempSettings, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(settingsFile, new
                {
                    UmbracoAppHost = new { RedirectToHttps = false },
                    Accounts = new { BackOfficeMemberGroups = Array.Empty<string>() },
                    Umbraco = new { CMS = new
                    {
                        Global = new { Id = Guid.NewGuid().ToString() },
                        Imaging = new { HMACSecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) },
                        Unattended = new
                        {
                            InstallUnattended = true, UpgradeUnattended = true, UnattendedTelemetryLevel = "Minimal",
                            UnattendedUserName = "Administrator", UnattendedUserEmail = "admin@localhost.local",
                            UnattendedUserPassword = "Start!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)) + "a9",
                        },
                    } },
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            File.Move(tempSettings, settingsPath);
        }
        finally { File.Delete(tempSettings); }
    }
}
