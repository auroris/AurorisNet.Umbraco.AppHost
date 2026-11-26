using IdentityServer.Directory;
using UmbracoAppHost.PeoplePicker;

namespace UmbracoAppHost.Testing;

/// <summary>
/// In-memory <see cref="IDirectoryLookup"/> and <see cref="IPeoplePickerDirectory"/> double for integration tests.
/// </summary>
public sealed class FakeDirectory : IDirectoryLookup, IPeoplePickerDirectory
{
    public bool Offline { get; set; } = true;
    public bool PickerOffline { get; set; }
    public PeoplePickerSettings? LastPickerSettings { get; private set; }
    public int SelectionChecks { get; private set; }
    public IReadOnlyList<DirectoryUser> Results { get; set; } = [];
    public DirectoryUserDetail? Found { get; set; }
    public Func<string, DirectoryUser?>? FallbackFind { get; set; }
    public DirectorySearchMode? LastMode { get; private set; }
    public DirectoryScope? LastScope { get; private set; }
    public IDirectoryUserFilter? LastFilter { get; private set; }
    public IReadOnlyList<string>? LastAttributes { get; private set; }

    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(
        string? query,
        int? take = null,
        DirectoryScope scope = DirectoryScope.Local,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default,
        IDirectoryUserFilter? filter = null,
        DirectorySearchMode mode = DirectorySearchMode.AddressBook)
    {
        LastMode = mode;
        LastScope = scope;
        LastFilter = filter;
        if (Offline) throw new DirectoryUnavailableException("Offline test directory.");
        return Task.FromResult(Results);
    }

    public Task<DirectoryUserDetail?> FindUserAsync(
        string id,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalAttributes = null)
    {
        LastAttributes = additionalAttributes;
        if (Offline) throw new DirectoryUnavailableException("Offline test directory.");
        return Task.FromResult(Found?.Sid == id ? Found : null);
    }

    public Task<IReadOnlyList<string>?> GetUserGroupsAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>?>(null);

    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string? query, int? take = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DirectoryGroup>>([]);

    public Task<DirectoryGroupDetail?> FindGroupAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<DirectoryGroupDetail?>(null);

    public Task<DirectoryGroupListing?> ListGroupMembersAsync(
        string group,
        string? query = null,
        int? take = null,
        DirectoryScope scope = DirectoryScope.Local,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult<DirectoryGroupListing?>(null);

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(PeoplePickerSettings settings, IReadOnlyList<string> terms, CancellationToken ct)
    {
        LastPickerSettings = settings;
        if (PickerOffline) throw new DirectoryUnavailableException("Offline test picker.");
        return Task.FromResult<IReadOnlyList<DirectoryUser>>(Results
            .Where(user => Eligible(settings, user)
                && terms.All(term => settings.SearchAttributes.Any(attribute =>
                    PeoplePickerSettings.Attribute(user, attribute)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)))
            .ToArray());
    }

    public Task<DirectoryUser?> FindAsync(PeoplePickerSettings settings, string sid, CancellationToken ct)
    {
        LastPickerSettings = settings;
        SelectionChecks++;
        if (PickerOffline) throw new DirectoryUnavailableException("Offline test picker.");
        var person = Found?.Sid == sid ? Found
            : Results.FirstOrDefault(user => user.Sid == sid)
            ?? FallbackFind?.Invoke(sid);
        return Task.FromResult(person is not null && Eligible(settings, person) ? person : null);
    }

    private static bool Eligible(PeoplePickerSettings settings, DirectoryUser user)
        => settings.Includes(user)
            && (!settings.AdditionalFilter.Contains("(givenName=*)") || !string.IsNullOrWhiteSpace(user.GivenName))
            && (!settings.AdditionalFilter.Contains("(!(displayName=*+*))") || !user.DisplayName.Contains('+'));

    public static DirectoryUser Person(
        string sid,
        string displayName,
        string? email = null,
        IReadOnlyDictionary<string, string>? attributes = null) => new()
    {
        Sid = sid,
        Guid = Guid.NewGuid().ToString(),
        AccountName = "TEST\\person",
        UserName = "person",
        DisplayName = displayName,
        GivenName = "Example",
        Enabled = true,
        Email = email ?? "person@example.invalid",
        Telephone = "555-0100",
        Attributes = attributes is null ? new Dictionary<string, string>() : new Dictionary<string, string>(attributes),
    };
}
