using AppBlocks.Setup;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using uSync.BackOffice;
using uSync.BackOffice.Configuration;
using uSync.BackOffice.SyncHandlers.Models;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Hosting;

/// <summary>
/// Published at each start once the site has the definitions of the build it runs: AppBlocks' and its companions', and
/// the shared ones of the saved export. An application handles it to install the content it owns and to run the steps
/// that change that content between releases, such as with AppBlocks' <c>ContentStepRunner</c>. It is published before
/// the site takes requests, and a handler that throws stops the start.
/// </summary>
public sealed class DefinitionsReadyNotification : INotification;

/// <summary>
/// Brings a site the definitions of the build it runs, at each start and before the site serves a page, then
/// publishes <see cref="DefinitionsReadyNotification"/>. AppBlocks installs its own, and updates the ones the build
/// changed when <c>AppBlocks:Definitions:UpdateAtStartup</c> is true, which this library's defaults set. The shared definitions of the saved export follow: document types, data types, templates,
/// dictionary items, languages, and media, member and relation types. A site imports them at the first start of each
/// build that ships other ones than it last imported: the files that differ are written to the working uSync folder,
/// and the folders of those definitions are imported. A new site does so once after its first boot, which imports
/// the dictionary only when it imports content.
/// </summary>
/// <remarks>
/// The fingerprint of the shared definitions a site last imported is one entry in Umbraco's key-value table,
/// <see cref="RecordKey"/>, so each build's definitions are imported once. An import that fails stops the start,
/// puts the working folder's files back and records nothing, so the next start imports again and a corrected build
/// starts from the files the site had. What the failed import had changed in the database stays changed. With
/// <see cref="Setting"/> false a start imports nothing, and an administrator imports from <b>Settings → uSync</b>.
/// </remarks>
public sealed partial class SiteDefinitions(IConfiguration configuration, IWebHostEnvironment environment, IOptions<uSyncSettings> settings,
    IEnumerable<ThemeLibrary> libraries, SchemaInstaller installer, BlockGridAllowances allowances, AppBlocksDefinitions definitions, ISyncService sync, ISyncHandlerFactory handlers,
    IKeyValueService keyValues, ICoreScopeProvider scopes, ContentStepRunner contentSteps, IEventAggregator events, ILogger<SiteDefinitions> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartingNotification>
{
    /// <summary>Whether a start imports the shared definitions of a build the site has not imported yet. It is true unless the site sets it.</summary>
    public const string Setting = "UmbracoAppHost:ImportDefinitions";

    /// <summary>The key-value entry that holds the fingerprint of the shared definitions the site last imported.</summary>
    public const string RecordKey = "UmbracoAppHost:SharedDefinitions";

    public async Task HandleAsync(UmbracoApplicationStartingNotification notification, CancellationToken cancellationToken)
    {
        // A site that waits to be installed or upgraded by hand has no definitions to bring up to date yet.
        if (notification.RuntimeLevel != RuntimeLevel.Run) return;
        await installer.EnsureAsync(cancellationToken);
        if (configuration.GetValue(Setting, true)) await ImportSharedAsync(cancellationToken);
        // An import puts the Block Grid data types back as the export has them, so the blocks registered from code are allowed again.
        await allowances.ApplyAsync(cancellationToken);
        await contentSteps.RunAsync("UmbracoAppHost", _ => Task.CompletedTask, ContentUpgrades, cancellationToken);
        await events.PublishAsync(new DefinitionsReadyNotification(), cancellationToken);
    }

    /// <summary>The fingerprint of the shared definitions the site last imported, or null when it has recorded none.</summary>
    public string? Imported => keyValues.GetValue(RecordKey);

    private async Task ImportSharedAsync(CancellationToken cancellationToken)
    {
        var working = Path.GetFullPath(settings.Value.RootFolder, environment.ContentRootPath);
        var files = USyncFiles.SharedDefinitions(USyncFiles.HostExport(configuration, environment.ContentRootPath, working), working, libraries);
        var fingerprint = USyncFiles.Fingerprint(files);
        var imported = Imported;
        if (imported == fingerprint) return;

        var changed = USyncFiles.Deliver(files, working);
        // uSync's export on save writes copies of the definitions AppBlocks installs, which an import skips.
        var removed = USyncFiles.RemoveCopies(working, definitions.Owns);
        // The handlers of the shared folders, whichever group uSync puts them in: the dictionary's is with content.
        var options = new SyncHandlerOptions { Action = HandlerActions.Import };
        var shared = handlers.GetValidHandlers(options)
            .Where(pair => USyncFiles.SharedFolders.Contains(pair.Handler.DefaultFolder, StringComparer.OrdinalIgnoreCase)).ToArray();
        logger.LogInformation("This build ships shared definitions {Fingerprint}, and this site last imported {Imported}. Wrote or removed {Changed} files in {Working}, removed {Removed} copies of AppBlocks' definitions, and importing {Handlers}. Files: {Files}",
            fingerprint[..12], imported is null ? "none on record" : imported[..Math.Min(12, imported.Length)], changed.Count, working, removed.Count,
            string.Join(", ", shared.Select(pair => pair.Handler.DefaultFolder)), string.Join(", ", changed.Select(file => file.Path)));
        uSyncAction[] actions;
        try
        {
            actions = (await sync.ImportAsync([working], false, shared, options, null)).ToArray();
            // A copy of an AppBlocks definition that an export wrote meanwhile is skipped, which is no failure.
            var failed = actions.Where(action => !action.Success && action.Message != AppBlocksDefinitionImport.Skipped).ToArray();
            if (failed.Length > 0)
                throw new InvalidOperationException($"{failed.Length} of this build's shared definitions could not be imported, so the site did not start: "
                    + string.Join(" ", failed.Select(action => $"{action.ItemType} {action.Name}: {action.Message ?? action.Exception?.Message}."))
                    + $" Nothing was recorded, so the next start imports again. To start the site and import from the backoffice instead, set {Setting} to false.");
        }
        catch
        {
            // The files go back, so a corrected build starts from what the site had and never meets this build's files.
            USyncFiles.Restore(working, changed);
            throw;
        }
        Record(fingerprint);
        logger.LogInformation("Imported this build's shared definitions: {Changed} of {Count} items changed. {Names}",
            actions.Count(action => action.Change > uSync.Core.ChangeType.NoChange), actions.Length,
            string.Join(", ", actions.Where(action => action.Change > uSync.Core.ChangeType.NoChange).Select(action => $"{action.ItemType} {action.Name} ({action.Change})")));
    }

    private void Record(string fingerprint)
    {
        using var scope = scopes.CreateCoreScope();
        // Before any read, so a SQLite read transaction is never upgraded while startup writes elsewhere.
        scope.EagerWriteLock(Constants.Locks.KeyValues);
        keyValues.SetValue(RecordKey, fingerprint);
        scope.Complete();
    }
}
