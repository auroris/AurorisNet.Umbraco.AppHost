using IdentityServer.Sso;
using Umbraco.Cms.Core.Events;
using uSync.BackOffice;

namespace UmbracoAppHost.Hosting;

/// <summary>
/// Creates the configured member and user groups before any uSync import, the first boot's included. That import runs
/// from uSync's <see cref="Umbraco.Cms.Core.Notifications.UmbracoApplicationStartingNotification"/> handler, before
/// the package's <see cref="ConfiguredAccounts"/> runs at the started notification, and a group picker drops a group
/// name that does not exist yet: a data source whose export names its write group would otherwise be published
/// writable by every reader.
/// </summary>
public sealed class ConfiguredGroupsBeforeImport(ConfiguredAccounts accounts) : INotificationAsyncHandler<uSyncImportStartingNotification>
{
    /// <summary>Creates the groups the <c>Accounts</c> section lists.</summary>
    public Task HandleAsync(uSyncImportStartingNotification notification, CancellationToken cancellationToken)
        => accounts.EnsureGroupsAsync();
}
