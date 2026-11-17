using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace UmbracoAppHost.Reports;

/// <summary>
/// Creates the <c>interactiveReportAdministrators</c> user group when it is missing, so that
/// <see cref="ReportAccountSettings.ReportAdministratorUserGroups"/> and a configured backoffice user can name it on
/// the first run. It is registered before the package's configured accounts, which seed those users.
/// </summary>
public sealed class ReportAdministratorsGroup(
    IRuntimeState runtimeState,
    IUserGroupService userGroupService,
    IShortStringHelper shortStringHelper,
    ILogger<ReportAdministratorsGroup> logger) : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    /// <summary>The group's alias, which the default <c>Accounts:ReportAdministratorUserGroups</c> lists.</summary>
    public const string Alias = "interactiveReportAdministrators";

    private static readonly Guid Key = new("6f1c0f3e-5d9a-4c2b-9a6e-2b4a7f3d0c04");

    /// <summary>Creates the group once the site runs. A failure is logged and leaves the site running.</summary>
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run) return;
        try
        {
            if (await userGroupService.GetAsync(Alias) is not null) return;
            var group = new UserGroup(shortStringHelper)
            {
                Key = Key,
                Alias = Alias,
                Name = "Interactive Report Administrators",
                Icon = "icon-bar-chart",
            };
            group.AddAllowedSection(Constants.Applications.Content);
            var result = await userGroupService.CreateAsync(group, Constants.Security.SuperUserKey, []);
            if (result.Success) logger.LogInformation("Created user group {Alias}.", Alias);
            else logger.LogWarning("Could not create user group {Alias}: {Status}.", Alias, result.Status);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Configured accounts: report administrators group failed.");
        }
    }
}
