using System.Globalization;
using System.Security.Claims;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using IdentityServer.Sso;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Authentication;

/// <summary>Resolves the verified Windows SID linked to an Umbraco member via the configured member SSO scheme.</summary>
public interface IMemberSidResolver
{
    string? WindowsMemberSid(DataSourceUser user);
    string? WindowsMemberSid(IMember member);
}

/// <summary>
/// Resolves a member's verified Windows SID from their external login for <see cref="IdentityServerSettings.MemberScheme"/>,
/// ensuring that the SID maps uniquely to that single member account.
/// </summary>
public sealed class WindowsMemberSidResolver(
    IMemberService members,
    IExternalLoginWithKeyService logins,
    IOptions<IdentityServerSettings> identity) : IMemberSidResolver
{
    public string? WindowsMemberSid(DataSourceUser user)
        => user.Member is { } member
            && int.TryParse(member.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && members.GetById(id) is { } account
            ? WindowsMemberSid(account)
            : null;

    public string? WindowsMemberSid(IMember member)
    {
        var scheme = identity.Value.MemberScheme;
        if (string.IsNullOrWhiteSpace(scheme)) return null;
        var sid = logins.GetExternalLogins(member.Key)
            .FirstOrDefault(login => login.LoginProvider == scheme)?.ProviderKey;
        return sid is not null && DirectoryMemberAccountKey(scheme, sid) == MemberKey(member) ? sid : null;
    }

    private string? DirectoryMemberAccountKey(string scheme, string sid)
    {
        var linked = logins.Find(scheme, sid)
            .Select(login => Guid.TryParse(login.UserId, out var key) ? members.GetById(key) : null)
            .OfType<IMember>()
            .DistinctBy(member => member.Key)
            .Take(2)
            .ToArray();
        return linked.Length == 1 ? MemberKey(linked[0]) : null;
    }

    private static string MemberKey(IMember member) => member.Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Decorates <see cref="IDataSourceUserResolver"/> to populate <see cref="DataSourceUser.Sid"/> from the member's
/// uniquely verified external login whenever the inner resolver left <see cref="DataSourceUser.Sid"/> null.
/// </summary>
internal sealed class VerifiedSidDataSourceUserResolver(
    IDataSourceUserResolver inner,
    IMemberSidResolver sids) : IDataSourceUserResolver
{
    public async Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
        => Enrich(await inner.ResolveAsync(principal, ct));

    public async Task<DataSourceUser?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
        => Enrich(await inner.ResolveAsync(context, ct));

    private DataSourceUser? Enrich(DataSourceUser? user)
    {
        if (user is not { IsMember: true, Sid: null }) return user;
        var sid = sids.WindowsMemberSid(user);
        return sid is null ? user : user with { Sid = sid };
    }
}
