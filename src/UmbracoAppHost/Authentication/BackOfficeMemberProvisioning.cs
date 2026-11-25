using IdentityServer.Sso;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>Creates or reuses the website member for a newly provisioned backoffice user.</summary>
internal sealed class BackOfficeMemberProvisioning(IMemberService members, IMemberManager manager,
    ICoreScopeProvider scopes, IOptions<AccountsSettings> accounts, IConfiguration configuration, IMemberGroupService? memberGroups = null) : IUserValidator<BackOfficeIdentityUser>
{
    // Return the conflict through Identity's normal validation response in the backoffice and SSO.
    public async Task<IdentityResult> ValidateAsync(UserManager<BackOfficeIdentityUser> userManager, BackOfficeIdentityUser user)
    {
        if (user.HasIdentity || user.UserName is null || user.Email is null) return IdentityResult.Success;
        var match = Match(user.UserName, user.Email, out _);
        if (!match.Succeeded) return match;
        if (memberGroups is not null)
        {
            var roles = ConfiguredRoles();
            if (roles.Length > 0)
            {
                var existing = (await memberGroups.GetAllAsync()).Select(g => g.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = roles.Where(role => !existing.Contains(role)).ToArray();
                if (missing.Length > 0)
                    return IdentityResult.Failed(new IdentityError
                    {
                        Code = "MissingMemberGroups",
                        Description = $"Configured Accounts:BackOfficeMemberGroups do not exist: {string.Join(", ", missing)}.",
                    });
            }
        }
        return IdentityResult.Success;
    }

    private string[] ConfiguredRoles() => (configuration.GetSection("Accounts:BackOfficeMemberGroups").Get<string[]>() ?? [])
        .Where(group => !string.IsNullOrWhiteSpace(group)).Select(group => group.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private IdentityResult Match(string username, string email, out IMember? member)
    {
        var matches = members.GetMembersByEmail(email).Append(members.GetByUsername(username)).OfType<IMember>()
            .DistinctBy(candidate => candidate.Key).Take(2).ToArray();
        member = matches.FirstOrDefault();
        if (matches.Length > 1)
            return IdentityResult.Failed(new IdentityError
            {
                Code = "ConflictingMembers",
                Description = $"Backoffice username '{username}' and email '{email}' match different members. Resolve the conflict in Members and retry.",
            });
        if (member is not null && (!member.IsApproved || member.IsLockedOut || AccountRetirement.IsRetired(member.Username)))
            return IdentityResult.Failed(new IdentityError
            {
                Code = "InactiveMember",
                Description = $"Matched member '{member.Username}' is disabled, locked out, or retired and cannot be reused for backoffice user '{username}'.",
            });

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> EnsureAsync(string username, string email, string? name)
    {
        using var scope = scopes.CreateCoreScope();
        scope.WriteLock(Constants.Locks.MemberTree);
        var match = Match(username, email, out var member);
        if (!match.Succeeded) return match;
        if (member is null)
        {
            member = members.CreateMemberWithIdentity(username, email, string.IsNullOrWhiteSpace(name) ? username : name,
                accounts.Value.MemberTypeAlias, isApproved: true);
            if (!member.HasIdentity)
                return IdentityResult.Failed(new IdentityError { Description = $"Creation of member '{username}' was cancelled." });
            await ProvisionedMemberPassword.InitializeAsync(member, manager);
        }

        var roles = ConfiguredRoles();
        if (roles.Length > 0)
        {
            members.AssignRoles([member.Id], roles);
            var missing = roles.Except(members.GetAllRoles(member.Id), StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
                return IdentityResult.Failed(new IdentityError { Description = $"Could not grant member '{member.Username}' the groups {string.Join(", ", missing)}." });
        }
        scope.Complete();
        return IdentityResult.Success;
    }
}
