using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations.Notifications;

namespace UmbracoAppHost.Authentication;

// The installer inserts the administrator directly. Wait for uSync's member type and the
// configured administrator username before provisioning, and run only for this installation.
internal sealed class InstalledAdministratorMember(IKeyValueService values, IRuntimeState runtime,
    IUserService users, BackOfficeMemberProvisioning members, ICoreScopeProvider scopes)
    : INotificationHandler<UnattendedInstallNotification>, INotificationHandler<DatabaseSchemaAndDataCreatedNotification>,
      INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string StateKey = "UmbracoAppHost.AdministratorMember";

    public void Handle(UnattendedInstallNotification notification) => values.SetValue(StateKey, "Pending");

    public void Handle(DatabaseSchemaAndDataCreatedNotification notification)
    {
        if (!notification.RequiresUpgrade) values.SetValue(StateKey, "Pending");
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtime.Level != RuntimeLevel.Run || values.GetValue(StateKey) != "Pending") return;
        using var scope = scopes.CreateCoreScope();
        var user = await users.GetAsync(Constants.Security.SuperUserKey)
            ?? throw new InvalidOperationException("The installation has no administrator.");
        var result = await members.EnsureAsync(user.Username, user.Email, user.Name);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        values.SetValue(StateKey, "Complete");
        scope.Complete();
    }
}
