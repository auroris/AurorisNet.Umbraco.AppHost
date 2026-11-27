using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using IdentityServer.Directory;

namespace UmbracoAppHost.PeoplePicker;

/// <summary>Queries both search and SID selection under the published picker's root and filters.</summary>
public interface IPeoplePickerDirectory
{
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(PeoplePickerSettings settings, IReadOnlyList<string> terms, CancellationToken ct);
    Task<DirectoryUser?> FindAsync(PeoplePickerSettings settings, string sid, CancellationToken ct);
}

public sealed class PeoplePickerDirectory(ILogger<PeoplePickerDirectory> logger, TimeProvider? clock = null) : IPeoplePickerDirectory
{
    /// <summary>
    /// Entries asked for per page. Excluded accounts are dropped here, after the directory answers, so one page may hold
    /// none of the people wanted. 200 keeps a page well under Active Directory's MaxPageSize (1000) and a search that
    /// fills its results early from fetching many entries it never reads.
    /// </summary>
    internal const int PageSize = 200;

    /// <summary>
    /// Candidates one search reads at most, ten pages: room for hundreds of excluded service or administrative accounts
    /// ahead of the people, while a term that matches most of the directory stops instead of walking all of it.
    /// </summary>
    internal const int MaxCandidates = 2000;

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(PeoplePickerSettings settings, IReadOnlyList<string> terms, CancellationToken ct)
        => Task.FromResult(Query(settings, Filter(settings, terms), 0, ct));

    public Task<DirectoryUser?> FindAsync(PeoplePickerSettings settings, string sid, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new DirectoryUnavailableException("Directory lookup requires Windows and a reachable domain.");
        byte[] bytes;
        try {
            var identity = new SecurityIdentifier(sid);
            bytes = new byte[identity.BinaryLength]; identity.GetBinaryForm(bytes, 0);
        }
        catch (ArgumentException) { throw new DirectoryQueryException("Choose a valid Windows SID."); }
        var assertion = "(objectSid=" + string.Concat(bytes.Select(b => "\\" + b.ToString("x2"))) + ")";
        return Task.FromResult(Query(settings, Filter(settings, [], assertion), 1, ct).SingleOrDefault());
    }

    internal static string Filter(PeoplePickerSettings settings, IReadOnlyList<string> terms, string? identity = null)
    {
        settings.Validate();
        if (identity is null) DirectoryQuery.EnsureGlobalTerms(terms, settings.MinTermLength);
        static string Escape(string text) => text.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
        return "(&(objectCategory=person)(objectClass=user)"
            + (settings.EnabledOnly ? "(!(userAccountControl:1.2.840.113556.1.4.803:=2))" : "")
            + settings.AdditionalFilter + identity
            + string.Concat(terms.Select(term => "(|" + string.Concat(settings.SearchAttributes.Select(attribute => "(" + attribute + "=*" + Escape(term) + "*)")) + ")")) + ")";
    }

    /// <summary>
    /// The eligible people among <paramref name="candidates"/>, in the directory's order. Reading stops at
    /// <see cref="PeoplePickerSettings.MaxResults"/> people, at the end of the candidates, after <see cref="MaxCandidates"/>
    /// candidates (a null one, an entry that is no person, counts too) or once the picker's timeout has passed since the
    /// search began; whoever was found by then is the answer. The directory's own time limits apply to each page, so a
    /// page already being fetched at the deadline can add up to one more timeout.
    /// </summary>
    internal static IReadOnlyList<DirectoryUser> Collect(IEnumerable<DirectoryUser?> candidates, PeoplePickerSettings settings,
        TimeProvider clock, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var people = new List<DirectoryUser>();
        var examined = 0;
        foreach (var person in candidates) {
            ct.ThrowIfCancellationRequested();
            if (person is not null && settings.Includes(person) && people.Count < settings.MaxResults) people.Add(person);
            if (people.Count == settings.MaxResults || ++examined == MaxCandidates || clock.GetUtcNow() >= deadline) break;
        }
        return people;
    }

    /// <summary>
    /// Runs <paramref name="filter"/>: a lookup of at most <paramref name="sizeLimit"/> entries, or with 0 a search read in
    /// pages of <see cref="PageSize"/> until <see cref="Collect"/> stops it.
    /// </summary>
    private IReadOnlyList<DirectoryUser> Query(PeoplePickerSettings settings, string filter, int sizeLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new DirectoryUnavailableException("Directory lookup requires Windows and a reachable domain.");
        try {
            var time = clock ?? TimeProvider.System;
            var distinguishedName = settings.DistinguishedName;
            if (distinguishedName.Length == 0) {
                using var domain = new DirectoryEntry("LDAP://RootDSE");
                distinguishedName = domain.Properties["defaultNamingContext"].Value as string
                    ?? throw new DirectoryUnavailableException("The default directory domain is unavailable.");
            }
            var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            using var root = new DirectoryEntry("LDAP://" + distinguishedName);
            using var search = new DirectorySearcher(root) {
                Filter = filter, SearchScope = settings.IncludeChildOus ? SearchScope.Subtree : SearchScope.OneLevel,
                SizeLimit = sizeLimit, PageSize = sizeLimit == 0 ? PageSize : 0, ClientTimeout = timeout,
                ServerTimeLimit = timeout, ServerPageTimeLimit = timeout, ReferralChasing = ReferralChasingOption.None,
            };
            search.PropertiesToLoad.AddRange(settings.Properties);
            // Leaving the enumeration early disposes the results, which abandons the rest of a paged search.
            using var found = search.FindAll();
            return Collect(found.Cast<SearchResult>().Select(entry => Person(settings, entry)), settings, time, ct);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException) {
            // Do not log search terms, SIDs or returned personal information.
            logger.LogWarning("People picker directory lookup failed ({ErrorType}).", error.GetType().Name);
            throw new DirectoryUnavailableException("The directory could not be reached. Try again later.");
        }
    }

    /// <summary>The person a search result describes, or null for an entry without a SID.</summary>
    private static DirectoryUser? Person(PeoplePickerSettings settings, SearchResult entry)
    {
        if (!OperatingSystem.IsWindows()) return null; // Query refused other platforms before any result could exist.
        object? Value(string name) => entry.Properties.Contains(name) && entry.Properties[name].Count > 0 ? entry.Properties[name][0] : null;
        string? Text(string name) => Value(name) is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        if (Value("objectSid") is not byte[] sid) return null;
        var username = Text("sAMAccountName") ?? "";
        return new DirectoryUser {
            Sid = new SecurityIdentifier(sid, 0).Value, Guid = Value("objectGUID") is byte[] guid ? new Guid(guid).ToString() : "",
            AccountName = username, UserName = username, DisplayName = Text("displayName") ?? username,
            GivenName = Text("givenName"), Surname = Text("sn"), Email = Text("mail"), Telephone = Text("telephoneNumber"),
            Enabled = (Convert.ToInt32(Value("userAccountControl") ?? 0) & 2) == 0,
            Attributes = settings.SearchAttributes.Concat(settings.ReturnedAttributes).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => Text(name) is not null).ToDictionary(name => name, name => Text(name)!, StringComparer.OrdinalIgnoreCase),
        };
    }
}
