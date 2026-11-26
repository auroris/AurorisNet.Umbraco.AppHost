using System.Security.Claims;
using AppBlocks.Authentication;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Authorizes a backoffice report-management request against Umbraco's Update permission on the hosting page,
/// unless the page is protected by a signed-in-member rule or belongs to a member-only branch configured in
/// <see cref="ProtectedPageAdmissionOptions"/>.
/// </summary>
public sealed class EditorPageAdmission(
    ApplicationAccessPolicy access,
    IUserService users,
    IContentPermissionService permissions,
    IPublicAccessService? publicAccess = null,
    IContentService? contents = null,
    IOptions<ProtectedPageAdmissionOptions>? options = null)
    : IProtectedPageAdmission
{
    public async Task<ClaimsPrincipal?> AdmitAsync(HttpContext context, IPublishedContent page, CancellationToken ct)
    {
        if (IsMemberOnly(page)) return null;
        if (await access.BackOfficeUserAsync(context) is not { } principal) return null;
        ct.ThrowIfCancellationRequested();
        return await CanEditAsync(principal, page) ? principal : null;
    }

    private bool IsMemberOnly(IPublishedContent page)
    {
        var rules = options?.Value;
        if (rules is null) return false;
        var entry = publicAccess?.GetEntryForContent(page.Path);
        if (rules.AdmitsSignedInMembers(entry)) return true;
        if (rules.MemberOnlyPageKeys.Count == 0) return false;
        if (rules.MemberOnlyPageKeys.Contains(page.Key)) return true;
        if (contents is not null)
        {
            if (entry is not null && contents.GetById(entry.ProtectedNodeId) is { } root && rules.MemberOnlyPageKeys.Contains(root.Key))
                return true;
            foreach (var segment in page.Path.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(segment, out var id) && id > 0 && contents.GetById(id) is { } ancestor && rules.MemberOnlyPageKeys.Contains(ancestor.Key))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the approved, not locked-out backoffice user the principal names has Umbraco's Update permission on the page.
    /// </summary>
    public async Task<bool> CanEditAsync(ClaimsPrincipal principal, IPublishedContent page)
    {
        var user = await ActiveUserAsync(principal);
        return user is not null
            && await permissions.AuthorizeAccessAsync(user, page.Key, ActionUpdate.ActionLetter) == ContentAuthorizationStatus.Success;
    }

    private async Task<IUser?> ActiveUserAsync(ClaimsPrincipal principal)
    {
        if (ApplicationAccessPolicy.BackOfficeIdentity(principal) is not { } identity) return null;
        var id = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var user = Guid.TryParse(id, out var key) ? await users.GetAsync(key)
            : int.TryParse(id, out var legacyId) ? users.GetUserById(legacyId) : null;
        return user is { IsApproved: true, IsLockedOut: false } ? user : null;
    }
}
