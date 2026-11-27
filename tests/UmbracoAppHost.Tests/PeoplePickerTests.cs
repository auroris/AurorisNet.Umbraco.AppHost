using System.Security.Claims;
using System.Text.Json;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using IdentityServer.Directory;
using UmbracoAppHost.PeoplePicker;

namespace UmbracoAppHost.Tests;

public sealed class PeoplePickerTests
{
    private static PeoplePickerSettings Settings => new() { EntireDomain = true, IdentityField = "personSid", NameField = "personName" };
    private static DirectoryUser Person => new() {
        Sid = "S-1-5-21-1-2-3-1001", Guid = Guid.NewGuid().ToString(), AccountName = "TEST\\person", UserName = "person",
        DisplayName = "Example Person", GivenName = "Example", Enabled = true,
    };
    private static PeoplePickerDefinition Picker(PeoplePickerSettings? settings = null) => new(Guid.NewGuid(), Guid.NewGuid(), settings ?? Settings);

    [Fact]
    public void Published_false_values_survive_reading_and_fingerprinting()
    {
        var values = new Dictionary<string, string> {
            ["entireDomain"] = "1", ["identityField"] = "sid", ["nameField"] = "name",
            ["includeChildOus"] = "0", ["enabledOnly"] = "False", ["allowManualEntry"] = "false",
        };
        var settings = PeoplePickerSettings.Read(key => values.GetValueOrDefault(key) ?? "");
        Assert.False(settings.IncludeChildOus); Assert.False(settings.EnabledOnly); Assert.False(settings.AllowManualEntry);
        Assert.Equal(settings.Fingerprint, settings.Fingerprint);
        Assert.NotEqual(settings.Fingerprint, (settings with { EnabledOnly = true }).Fingerprint);
        Assert.DoesNotContain("Fingerprint", JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("searchRoot", "https://other.example/people")]
    [InlineData("searchAttributes", "displayName)(objectClass=*)")]
    [InlineData("maxResults", "50000")]
    [InlineData("timeoutSeconds", "0")]
    [InlineData("cacheMinutes", "1441")]
    [InlineData("additionalFilter", "(givenName=*)(mail=*)")]
    [InlineData("prefillMappings", "unreturnedAttribute=email")]
    [InlineData("prefillMappings", "mail=sid")]
    public void Invalid_published_configuration_is_refused(string key, string value)
    {
        var values = new Dictionary<string, string> { ["entireDomain"] = "1", ["identityField"] = "sid", ["nameField"] = "name", [key] = value };
        Assert.Throws<DirectoryQueryException>(() => PeoplePickerSettings.Read(name => values.GetValueOrDefault(name) ?? ""));
    }

    [Fact]
    public void Ldap_terms_are_escaped_and_enabled_and_editor_filters_both_apply()
    {
        var filter = PeoplePickerDirectory.Filter(Settings with { AdditionalFilter = "(givenName=*)" }, ["A*)(mail=*)"]);
        Assert.Contains("(displayName=*A\\2a\\29\\28mail=\\2a\\29*)", filter);
        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=2))", filter);
        Assert.Contains("(givenName=*)", filter);
    }

    [Fact]
    public void Optional_groups_accept_members_and_users_independently_without_a_sid()
    {
        var member = new DataSourceUser("member", null, "Member", null, ["Requesters"], false);
        var admin = new DataSourceUser("backoffice:1", null, "Admin", null, ["Administrators"], true) { UserGroupAliases = ["admin"] };
        var settings = Settings with { AllowedMemberGroups = ["Requesters"], AllowedUserGroups = ["admin"] };
        Assert.True(PeoplePickerService.Permits(settings, member)); Assert.True(PeoplePickerService.Permits(settings, admin));
        Assert.False(PeoplePickerService.Permits(settings, DataSourceUser.Anonymous));
        Assert.False(PeoplePickerService.Permits(Settings, DataSourceUser.Anonymous));
        Assert.True(PeoplePickerService.Permits(Settings with { AllowAnonymous = true }, DataSourceUser.Anonymous));
        Assert.True(PeoplePickerService.Permits(Settings, member));
        Assert.False(PeoplePickerService.Permits(settings with { AllowedUserGroups = ["Administrators"] }, admin));
    }

    [Fact]
    public void Cache_is_isolated_by_page_block_settings_and_age()
    {
        var clock = new Clock(); using var cache = new PeoplePickerCache(clock);
        var picker = Picker(Settings with { AllowCachedSelection = true, CacheMinutes = 1 });
        cache.Store(picker, Person);
        Assert.Single(cache.Read(picker));
        Assert.Empty(cache.Read(picker with { PageKey = Guid.NewGuid() }));
        Assert.Empty(cache.Read(picker with { BlockKey = Guid.NewGuid() }));
        Assert.Empty(cache.Read(picker with { Settings = picker.Settings with { SearchRoot = "OU=Other,DC=example,DC=com" } }));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Empty(cache.Read(picker));
    }

    [Fact]
    public async Task A_live_rejection_removes_cached_eligibility_before_an_outage()
    {
        var clock = new Clock(); using var cache = new PeoplePickerCache(clock);
        var directory = new FakeDirectory { Person = Person };
        var service = new PeoplePickerService(directory, cache, new Users(), []);
        var picker = Picker(Settings with { AllowCachedSelection = true });
        Assert.NotNull(await service.SelectAsync(picker, directory.Person.Sid, TestContext.Current.CancellationToken));
        directory.Offline = true;
        Assert.True((await service.SelectAsync(picker, directory.Person.Sid, TestContext.Current.CancellationToken))!.Cached);
        directory.Offline = false;
        directory.Person = directory.Person with { Enabled = false };
        Assert.Null(await service.SelectAsync(picker, directory.Person.Sid, TestContext.Current.CancellationToken));
        directory.Offline = true;
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => service.SelectAsync(picker, directory.Person.Sid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_is_opt_in_and_searches_never_mark_a_form_identity_validated()
    {
        using var cache = new PeoplePickerCache(new Clock()); var directory = new FakeDirectory { Person = Person };
        var service = new PeoplePickerService(directory, cache, new Users(), []); var picker = Picker();
        Assert.Single((await service.SearchAsync(picker, "Example", TestContext.Current.CancellationToken)).People);
        Assert.False(service.WasValidated(picker.BlockKey, picker.Settings.IdentityField, directory.Person.Sid));
        directory.Offline = true;
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => service.SelectAsync(picker, directory.Person.Sid, TestContext.Current.CancellationToken));
    }

    private static IEnumerable<DirectoryUser?> Candidates(int excluded, int eligible, Action? read = null)
    {
        for (var i = 0; i < excluded + eligible; i++)
        {
            read?.Invoke();
            yield return Person with { Sid = "S-1-5-21-1-2-3-" + (2000 + i), UserName = i < excluded ? "svc-" + i : "person-" + i };
        }
    }

    [Fact]
    public void A_search_reads_past_excluded_accounts_until_it_has_enough_people()
    {
        var settings = Settings with { ExcludedAccountPattern = "^svc-", MaxResults = 5 };
        var read = 0;
        var people = PeoplePickerDirectory.Collect(Candidates(600, 50, () => read++), settings, new Clock(), TestContext.Current.CancellationToken);
        Assert.Equal(["person-600", "person-601", "person-602", "person-603", "person-604"], people.Select(p => p.UserName));
        Assert.Equal(605, read);
        // Entries without a SID count as candidates but are never people.
        Assert.Equal(2, PeoplePickerDirectory.Collect([null, Person, null, Person with { UserName = "other" }], settings, new Clock(),
            TestContext.Current.CancellationToken).Count);
    }

    [Fact]
    public void A_search_stops_at_the_end_of_the_results_the_candidate_cap_or_the_timeout()
    {
        var settings = Settings with { ExcludedAccountPattern = "^svc-", MaxResults = 5, TimeoutSeconds = 3 };
        Assert.Equal(2, PeoplePickerDirectory.Collect(Candidates(10, 2), settings, new Clock(), TestContext.Current.CancellationToken).Count);

        var read = 0;
        Assert.Empty(PeoplePickerDirectory.Collect(Candidates(PeoplePickerDirectory.MaxCandidates, 5, () => read++), settings, new Clock(), TestContext.Current.CancellationToken));
        Assert.Equal(PeoplePickerDirectory.MaxCandidates, read);

        // The timeout is for the whole search: whoever was found before it passed is the answer.
        var clock = new Clock();
        read = 0;
        var late = PeoplePickerDirectory.Collect(Candidates(0, 50, () => { if (++read > 2) clock.Now += TimeSpan.FromSeconds(2); }), settings, clock,
            TestContext.Current.CancellationToken);
        Assert.Equal(["person-0", "person-1", "person-2", "person-3"], late.Select(p => p.UserName));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Users : IDataSourceUserResolver
    {
        public Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => Task.FromResult<DataSourceUser?>(null);
        public Task<DataSourceUser?> ResolveAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult<DataSourceUser?>(null);
    }
    private sealed class FakeDirectory : IPeoplePickerDirectory
    {
        public required DirectoryUser Person { get; set; }
        public bool Offline;
        public Task<IReadOnlyList<DirectoryUser>> SearchAsync(PeoplePickerSettings settings, IReadOnlyList<string> terms, CancellationToken ct)
            => Offline ? throw new DirectoryUnavailableException("Test outage") : Task.FromResult<IReadOnlyList<DirectoryUser>>([Person]);
        public Task<DirectoryUser?> FindAsync(PeoplePickerSettings settings, string sid, CancellationToken ct)
            => Offline ? throw new DirectoryUnavailableException("Test outage") : Task.FromResult<DirectoryUser?>(Person);
    }
}
