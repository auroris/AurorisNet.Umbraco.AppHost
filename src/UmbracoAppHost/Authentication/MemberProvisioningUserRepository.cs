using System.Linq.Expressions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;

namespace UmbracoAppHost.Authentication;

/// <summary>Identity, directory provisioning and direct IUserService saves all insert users here,
/// after cancellable notifications and inside the caller's database transaction.</summary>
internal sealed class MemberProvisioningUserRepository(IUserRepository inner, IServiceScopeFactory services) : IUserRepository
{
    public void Save(IUser user)
    {
        if (!user.HasIdentity)
        {
            using var scope = services.CreateScope();
            var result = scope.ServiceProvider.GetRequiredService<BackOfficeMemberProvisioning>()
                .EnsureAsync(user.Username, user.Email, user.Name).GetAwaiter().GetResult();
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error => error.Description)));
        }
        inner.Save(user);
    }

    public void Delete(IUser user) => inner.Delete(user);
    public IUser? Get(Guid id) => inner.Get(id);
    public IUser? Get(int id) => inner.Get(id);
    public IUser? Get(int? id, bool includeSecurityData) => inner.Get(id, includeSecurityData);
    public IEnumerable<IUser> GetMany(params Guid[]? ids) => inner.GetMany(ids);
    public bool Exists(Guid id) => inner.Exists(id);
    public bool ExistsByLogin(string login) => inner.ExistsByLogin(login);
    public bool ExistsByUserName(string username) => inner.ExistsByUserName(username);
    public IEnumerable<IUser> Get(IQuery<IUser> query) => inner.Get(query);
    public int Count(IQuery<IUser>? query) => inner.Count(query);
    public int GetCountByQuery(IQuery<IUser>? query) => inner.GetCountByQuery(query);
    public IEnumerable<IUser> GetAllInGroup(int groupId) => inner.GetAllInGroup(groupId);
    public IEnumerable<IUser> GetAllNotInGroup(int groupId) => inner.GetAllNotInGroup(groupId);
    public IUser? GetByUsername(string username, bool includeSecurityData) => inner.GetByUsername(username, includeSecurityData);
    public IUser? GetForUpgradeByUsername(string username) => inner.GetForUpgradeByUsername(username);
    public IUser? GetForUpgradeByEmail(string email) => inner.GetForUpgradeByEmail(email);
    public IUser? GetForUpgrade(int id) => inner.GetForUpgrade(id);
    public IProfile? GetProfile(string username) => inner.GetProfile(username);
    public IProfile? GetProfile(int id) => inner.GetProfile(id);
    public IDictionary<UserState, int> GetUserStates() => inner.GetUserStates();
    public Guid CreateLoginSession(int? userId, string requestingIpAddress, bool cleanStaleSessions = true)
        => inner.CreateLoginSession(userId, requestingIpAddress, cleanStaleSessions);
    public bool ValidateLoginSession(int userId, Guid sessionId) => inner.ValidateLoginSession(userId, sessionId);
    public int ClearLoginSessions(int userId) => inner.ClearLoginSessions(userId);
    public int ClearLoginSessions(TimeSpan timespan) => inner.ClearLoginSessions(timespan);
    public void ClearLoginSession(Guid sessionId) => inner.ClearLoginSession(sessionId);
    public IEnumerable<string> GetAllClientIds() => inner.GetAllClientIds();
    public IEnumerable<string> GetClientIds(int id) => inner.GetClientIds(id);
    public void AddClientId(int id, string clientId) => inner.AddClientId(id, clientId);
    public bool RemoveClientId(int id, string clientId) => inner.RemoveClientId(id, clientId);
    public IUser? GetByClientId(string clientId) => inner.GetByClientId(clientId);
    public void InvalidateSessionsForRemovedProviders(IEnumerable<string> currentLoginProviders)
        => inner.InvalidateSessionsForRemovedProviders(currentLoginProviders);

    [Obsolete("Use the overload accepting userKinds.")]
    public IEnumerable<IUser> GetPagedResultsByQuery(IQuery<IUser>? query, long pageIndex, int pageSize, out long totalRecords,
        Expression<Func<IUser, object?>> orderBy, Direction orderDirection = Direction.Ascending, string[]? includeUserGroups = null,
        string[]? excludeUserGroups = null, UserState[]? userState = null, IQuery<IUser>? filter = null)
        => inner.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, orderBy, orderDirection,
            includeUserGroups, excludeUserGroups, userState, filter);

    public IEnumerable<IUser> GetPagedResultsByQuery(IQuery<IUser>? query, long pageIndex, int pageSize, out long totalRecords,
        Expression<Func<IUser, object?>> orderBy, Direction orderDirection, string[]? includeUserGroups,
        string[]? excludeUserGroups, UserState[]? userState, UserKind[]? userKinds, IQuery<IUser>? filter = null)
        => inner.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, orderBy, orderDirection,
            includeUserGroups, excludeUserGroups, userState, userKinds, filter);
}
