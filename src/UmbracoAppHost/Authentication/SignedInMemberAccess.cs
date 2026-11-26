using System.Security.Claims;
using AppBlocks.Authentication;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>Public Access rules and options for pages that admit any signed-in member or require member-only access.</summary>
public static class SignedInMemberAccess
{
    /// <summary>The default Public Access <see cref="PublicAccessRule.RuleType"/> that admits any signed-in member.</summary>
    public const string RuleType = "AppHostSignedInMember";

    /// <summary>Registers an additional Public Access <see cref="PublicAccessRule.RuleType"/> that admits any authenticated member.</summary>
    public static IServiceCollection AddSignedInMemberRuleType(this IServiceCollection services, string ruleType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleType);
        return services.Configure<ProtectedPageAdmissionOptions>(options => options.SignedInMemberRuleTypes.Add(ruleType));
    }

    /// <summary>
    /// Marks <paramref name="pageKeys"/> (and all of their descendants) as member-only so backoffice editors are not
    /// admitted past Public Access without member group membership.
    /// </summary>
    public static IServiceCollection RequireMemberOnlyAccess(this IServiceCollection services, params Guid[] pageKeys)
    {
        ArgumentNullException.ThrowIfNull(pageKeys);
        return services.Configure<ProtectedPageAdmissionOptions>(options =>
        {
            foreach (var key in pageKeys) options.MemberOnlyPageKeys.Add(key);
        });
    }
}

/// <summary>Configures signed-in-member Public Access rule types and member-only protected page branches.</summary>
public sealed class ProtectedPageAdmissionOptions
{
    /// <summary>Public Access rule types that admit any authenticated Umbraco member.</summary>
    public HashSet<string> SignedInMemberRuleTypes { get; } = new(StringComparer.Ordinal) { SignedInMemberAccess.RuleType };

    /// <summary>Page keys (and all descendants) where backoffice editors cannot bypass Public Access.</summary>
    public HashSet<Guid> MemberOnlyPageKeys { get; } = [];

    internal bool AdmitsSignedInMembers(PublicAccessEntry? entry)
        => entry?.Rules.Any(rule => rule.RuleType is not null && SignedInMemberRuleTypes.Contains(rule.RuleType)) == true;
}

/// <summary>
/// Decorates <see cref="IPublicAccessChecker"/> so a Public Access entry containing a configured signed-in-member
/// rule type admits any authenticated Umbraco member.
/// </summary>
internal sealed class AppHostPublicAccessChecker(
    IPublicAccessChecker inner,
    IHttpContextAccessor http,
    IPublicAccessService access,
    IContentService contents,
    IOptions<ProtectedPageAdmissionOptions> options) : IPublicAccessChecker
{
    public Task<PublicAccessStatus> HasMemberAccessToContentAsync(int publishedContentId)
        => HasMemberAccessToContentAsync(publishedContentId, http.HttpContext?.User ?? new ClaimsPrincipal());

    public async Task<PublicAccessStatus> HasMemberAccessToContentAsync(int publishedContentId, ClaimsPrincipal claimsPrincipal)
    {
        var status = await inner.HasMemberAccessToContentAsync(publishedContentId, claimsPrincipal);
        if (status == PublicAccessStatus.AccessDenied
            && ApplicationAccessPolicy.MemberIdentity(claimsPrincipal) is not null
            && contents.GetById(publishedContentId) is { } page
            && options.Value.AdmitsSignedInMembers(access.GetEntryForContent(page.Path)))
            return PublicAccessStatus.AccessAccepted;
        return status;
    }
}
