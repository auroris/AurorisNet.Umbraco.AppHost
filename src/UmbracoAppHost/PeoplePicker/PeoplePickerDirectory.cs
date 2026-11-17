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

public sealed class PeoplePickerDirectory(ILogger<PeoplePickerDirectory> logger) : IPeoplePickerDirectory
{
    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(PeoplePickerSettings settings, IReadOnlyList<string> terms, CancellationToken ct)
        => Task.FromResult(Query(settings, Filter(settings, terms), settings.MaxResults + 50, ct));

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

    private IReadOnlyList<DirectoryUser> Query(PeoplePickerSettings settings, string filter, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new DirectoryUnavailableException("Directory lookup requires Windows and a reachable domain.");
        try {
            var distinguishedName = settings.DistinguishedName;
            if (distinguishedName.Length == 0) {
                using var domain = new DirectoryEntry("LDAP://RootDSE");
                distinguishedName = domain.Properties["defaultNamingContext"].Value as string
                    ?? throw new DirectoryUnavailableException("The default directory domain is unavailable.");
            }
            using var root = new DirectoryEntry("LDAP://" + distinguishedName);
            using var search = new DirectorySearcher(root) {
                Filter = filter, SearchScope = settings.IncludeChildOus ? SearchScope.Subtree : SearchScope.OneLevel,
                SizeLimit = limit, ClientTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
                ServerTimeLimit = TimeSpan.FromSeconds(settings.TimeoutSeconds), ReferralChasing = ReferralChasingOption.None,
            };
            search.PropertiesToLoad.AddRange(settings.Properties);
            using var found = search.FindAll();
            var people = new List<DirectoryUser>();
            foreach (SearchResult entry in found) {
                ct.ThrowIfCancellationRequested();
                object? Value(string name) => entry.Properties.Contains(name) && entry.Properties[name].Count > 0 ? entry.Properties[name][0] : null;
                string? Text(string name) => Value(name) is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
                if (Value("objectSid") is not byte[] sid) continue;
                var username = Text("sAMAccountName") ?? "";
                var person = new DirectoryUser {
                    Sid = new SecurityIdentifier(sid, 0).Value, Guid = Value("objectGUID") is byte[] guid ? new Guid(guid).ToString() : "",
                    AccountName = username, UserName = username, DisplayName = Text("displayName") ?? username,
                    GivenName = Text("givenName"), Email = Text("mail"), Telephone = Text("telephoneNumber"),
                    Enabled = (Convert.ToInt32(Value("userAccountControl") ?? 0) & 2) == 0,
                    Attributes = settings.SearchAttributes.Concat(settings.ReturnedAttributes).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(name => Text(name) is not null).ToDictionary(name => name, name => Text(name)!, StringComparer.OrdinalIgnoreCase),
                };
                if (settings.Includes(person)) people.Add(person);
                if (people.Count == settings.MaxResults) break;
            }
            return people;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException) {
            // Do not log search terms, SIDs or returned personal information.
            logger.LogWarning("People picker directory lookup failed ({ErrorType}).", error.GetType().Name);
            throw new DirectoryUnavailableException("The directory could not be reached. Try again later.");
        }
    }
}
