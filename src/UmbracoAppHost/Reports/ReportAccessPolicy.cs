using System.Security.Claims;
using InteractiveReport.AspNetCore;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using IdentityServer.Sso;
using AppBlocks.Authentication;
using AppBlocks.DataSources;
using AppBlocks.InteractiveReports;
using AppBlocks.InteractiveReports.Blocks;
using AppBlocks.InteractiveReports.DataSources;
using AppBlocks.InteractiveReports.Reports;
using AppBlocks.InteractiveReports.Setup;

namespace UmbracoAppHost.Reports;

/// <summary>
/// Decides who administers and reads Interactive Reports by bridging Umbraco's two identities.
/// </summary>
/// <remarks>
/// A backoffice user is an administrator when, approved and not locked out, one of their user groups
/// is Umbraco's <c>admin</c> group, AppBlocks' <c>interactiveReportsAdministrator</c> group, or listed
/// in <see cref="ReportAccountSettings.ReportAdministratorUserGroups"/> or AppBlocks'
/// <c>AppBlocks:ReportAdministratorGroups</c>: the groups AppBlocks' own policy accepts, plus this
/// host's configured ones. A member is an administrator when the account they signed in with is
/// also linked to such a backoffice user: the provider key stored with the external login (the
/// Windows SID, whichever provider supplied it) joins the two. That link is this host's choice as
/// a single sign-on host; AppBlocks never relates the two account stores.
/// Everyone else signed in is an ordinary report user, subject to any per-report <c>Access</c>
/// member groups configured on a published report definition node. An anonymous visitor is refused
/// every report unless <see cref="ReportAccountSettings.AllowAnonymousReportBlocks"/> is on; then
/// they get AppBlocks' own answer, <see cref="AnonymousReportAccess.Allows"/>: a report block on a
/// page without Public Access, whose SQL binds nobody, may be viewed, queried and exported, and
/// every other report still needs an account. A saved view belongs to the one identity AppBlocks'
/// <see cref="DefaultReportAccessPolicy.OwnerKey"/> names: the member when one is signed in, else
/// the backoffice user. As in AppBlocks' own policy, a view's owner, or anyone for a global or default
/// view, reads it, and its owner changes or deletes it, only while they may read the report itself;
/// an administrator may do all three.
/// </remarks>
public sealed class ReportAccessPolicy(
    IUserService userService,
    IMemberService memberService,
    IEntityService entityService,
    IExternalLoginWithKeyService externalLogins,
    SsoProviderCatalog providers,
    IOptions<ReportAccountSettings> accounts,
    IOptions<AppBlocksOptions> appBlocks,
    IOptions<InteractiveReportOptions> reportOptions,
    ILogger<ReportAccessPolicy> logger,
    PublishedReportCatalog? definitions = null,
    IDataSourceUserResolver? dataSourceUsers = null,
    IMemberGroupService? memberGroups = null) : IReportAccessPolicy
{
    /// <summary>Applies the Interactive Reports action/resource decision table for one request.</summary>
    public async ValueTask<bool> AuthorizeAsync(InteractiveReportAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = request.User;
        // The definition store has already refused an anonymous visitor a block whose page has Public Access or whose
        // source needs an account. With the site's consent what is left may be read, as on a plain AppBlocks site.
        if (user.Identity?.IsAuthenticated != true)
            return accounts.Value.AllowAnonymousReportBlocks && AnonymousReportAccess.Allows(request);
        // Published block settings own their starting view. Saved personal views remain available.
        if (ReportBlocks.ParseName(request.Resource.ReportName, out _, out _)
            && request.Action == InteractiveReportAction.SelectDefaultReport) return false;

        var isAdministrator = await IsAdministratorAsync(user);
        var saved = request.Resource.SavedReport;
        var caller = DefaultReportAccessPolicy.OwnerKey(user, reportOptions.Value.IdentityClaim);
        var isOwner = saved is not null && caller is not null
            && string.Equals(saved.Owner, caller, StringComparison.Ordinal);
        var isPublic = saved is { IsGlobal: true } or { IsDefault: true };

        var reportName = request.Resource.SourceReportName ?? request.Resource.ReportName;
        var allowed = request.Action switch
        {
            InteractiveReportAction.ViewReport
                or InteractiveReportAction.Query
                or InteractiveReportAction.Export
                or InteractiveReportAction.ListSavedReports
                or InteractiveReportAction.CreateSavedReport =>
                isAdministrator || await CanReadReportAsync(reportName, user, cancellationToken),

            // A saved view shows the report's rows, so someone who may no longer read the report, such as a member removed from
            // its groups, keeps no way into it through their own views or the shared ones.
            InteractiveReportAction.ReadSavedReport =>
                isAdministrator || (isPublic || isOwner) && await CanReadReportAsync(reportName, user, cancellationToken),

            InteractiveReportAction.UpdateSavedReport
                or InteractiveReportAction.DeleteSavedReport =>
                isAdministrator || isOwner && await CanReadReportAsync(reportName, user, cancellationToken),

            _ => isAdministrator,
        };
        logger.LogDebug(
            "Report authorization: {Action} on {Report} for {Identity} ({AuthenticationType}) administrator={Administrator} owner={Owner} public={Public} => {Allowed}; administrator groups [{Groups}]",
            request.Action, request.Resource.ReportName, caller, user.Identity?.AuthenticationType,
            isAdministrator, isOwner, isPublic, allowed, string.Join(", ", AdministratorGroups()));
        return allowed;
    }

    private async Task<bool> CanReadReportAsync(string reportName, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (reportName.StartsWith("__", StringComparison.Ordinal))
            return false;
        if (definitions is not null) await definitions.LoadAsync();
        if ((definitions?.Reports ?? reportOptions.Value.Reports).TryGetValue(reportName, out var definition)
            && definition.Authorization?.Policy == ReportAdministratorRequirement.Policy)
            return false;

        if (definitions?.ReportNodes.GetValueOrDefault(reportName)?.MemberGroups is not { } picked)
            return true;
        if (memberGroups is null || dataSourceUsers is null)
            return false;
        var reader = await DataSourceQueries.ResolveReaderAsync(dataSourceUsers, user, cancellationToken);
        if (reader is not { IsAnonymous: false })
            return false;
        var allowedGroups = await MemberGroupNames.ResolveAsync(memberGroups, picked);
        return allowedGroups.Any(reader.IsInMemberGroup);
    }

    /// <summary>Whether the principal, backoffice user or member, administers reports.</summary>
    public async Task<bool> IsAdministratorAsync(ClaimsPrincipal user)
    {
        // A backoffice user answers for themselves, alone or beside a member.
        if (ApplicationAccessPolicy.BackOfficeIdentity(user)?.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId
            && IsAdministrator(await FindUserAsync(userId)))
            return true;

        // A member is one when the backoffice user their sign-in links to is.
        var member = ApplicationAccessPolicy.MemberIdentity(user)
            ?? user.Identities.FirstOrDefault(identity => identity.IsAuthenticated && !IsBackOffice(identity));
        var memberId = member?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return memberId is not null && IsAdministrator(await FindLinkedBackOfficeUserAsync(memberId));
    }

    private static bool IsBackOffice(ClaimsIdentity identity) => identity.AuthenticationType
        is Constants.Security.BackOfficeAuthenticationType or Constants.Security.BackOfficeExposedAuthenticationType;

    /// <summary>The configured group aliases that administer reports, beyond Umbraco's <c>admin</c> and AppBlocks' own group.</summary>
    private IEnumerable<string> AdministratorGroups()
        => accounts.Value.ReportAdministratorUserGroups.Concat(appBlocks.Value.ReportAdministratorGroups);

    private bool IsAdministrator(IUser? user)
    {
        if (user is not { IsApproved: true, IsLockedOut: false }) return false;
        var configured = AdministratorGroups().ToList();
        return user.Groups.Any(group => group.Alias.Equals(Constants.Security.AdminGroupAlias, StringComparison.OrdinalIgnoreCase)
            || group.Alias.Equals(ReportAdministratorGroup.Alias, StringComparison.OrdinalIgnoreCase)
            || configured.Contains(group.Alias, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IUser?> FindUserAsync(string id)
    {
        if (Guid.TryParse(id, out var key))
            return await userService.GetAsync(key);
        return int.TryParse(id, out var legacyId) ? userService.GetUserById(legacyId) : null;
    }

    /// <summary>
    /// Follows member → provider key → backoffice user through the external-login table.
    /// Does not match by unverified email address.
    /// </summary>
    private async Task<IUser?> FindLinkedBackOfficeUserAsync(string memberId)
    {
        // The member cookie carries the integer member id as NameIdentifier; external logins are keyed by GUID.
        var member = Guid.TryParse(memberId, out var key)
            ? entityService.GetId(key, UmbracoObjectTypes.Member) is { Success: true } id ? memberService.GetById(id.Result) : null
            : int.TryParse(memberId, out var legacyId) ? memberService.GetById(legacyId) : null;
        if (member is not { IsApproved: true, IsLockedOut: false })
            return null;

        // Match each member external login only against backoffice providers sharing the same provider name (or Windows SID).
        var memberLogins = externalLogins.GetExternalLogins(member.Key)
            .Where(login => login.LoginProvider.StartsWith(Constants.Security.MemberExternalAuthenticationTypePrefix, StringComparison.Ordinal))
            .ToArray();
        foreach (var login in memberLogins)
        {
            var memberSuffix = login.LoginProvider[Constants.Security.MemberExternalAuthenticationTypePrefix.Length..];
            var matchingProviders = providers.BackOffice.Where(p =>
                p.BackOfficeScheme.EndsWith(memberSuffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            var targets = matchingProviders.Length > 0 ? matchingProviders : providers.BackOffice;
            foreach (var provider in targets)
            {
                var backOfficeLogin = externalLogins.Find(provider.BackOfficeScheme, login.ProviderKey).FirstOrDefault();
                var linked = backOfficeLogin is null ? null : await FindUserAsync(backOfficeLogin.UserId);
                if (linked is null) continue;
                logger.LogDebug("Member {Member} maps to backoffice user {UserName} through the {Scheme} key.", member.Username, linked.Username, provider.BackOfficeScheme);
                return linked;
            }
        }
        return null;
    }
}
