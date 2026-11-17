using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using IdentityServer.Sso;
using DirectoryAccounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Accounts created from the directory carry the person's Windows SID from the start. This machine
/// is not in a domain, so its directory is its local accounts, and the person searched for and
/// given accounts is whoever runs the tests.
/// </summary>
[Collection("Umbraco hosts")]
[SupportedOSPlatform("windows")]
public sealed class DirectoryAccountsTests(AccountsTestHost host) : IClassFixture<AccountsTestHost>
{
    private const string Api = "/umbraco/management/api/v1/directory";
    private const string Domain = "Umbraco.Domain";
    private const string DomainMembers = "UmbracoMembers.Domain";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The account policy with first-sign-in matching by username or email turned off.</summary>
    private static readonly AccountsSettings Strict = new() { LinkByUserNameOnFirstSignIn = false };

    private static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;

    private static string CurrentAccountName => WindowsIdentity.GetCurrent().Name;

    private static string CurrentUserName => Environment.UserName.ToLowerInvariant();

    [Fact]
    public async Task A_user_created_from_the_directory_carries_the_SID_and_signs_in_by_it_alone()
    {
        var sid = CurrentSid;
        if (AccountDirectorySettings.IsOutsideDomain)
            Assert.IsType<LocalAccountDirectory>(host.App.Services.GetRequiredService<IAccountDirectory>());
        using var administrator = await host.SignInAsAdministratorAsync();

        // The search finds the person running the tests, who has no user yet.
        var before = await SearchAsync(administrator, "users", Environment.UserName);
        var person = Assert.Single(before.Results, result => result.Sid == sid);
        Assert.Equal(CurrentAccountName, person.AccountName, ignoreCase: true);
        Assert.True(person.Enabled);
        Assert.DoesNotContain(sid, before.Existing.Keys);

        // Created with the platform's username, the directory's name and the email fallback, in the
        // chosen group, and linked under every backoffice scheme before anyone signs in.
        using var response = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken);
        var created = await ReadAsync<CreatedAccount>(response, HttpStatusCode.Created);
        Assert.Equal(CurrentUserName, created.Username);
        Assert.Equal(person.DisplayName, created.Name);
        Assert.Equal(person.Email ?? $"{CurrentUserName}@localhost", created.Email);
        var user = (await host.Users.GetAsync(created.Key))!;
        Assert.False(string.IsNullOrEmpty(user.RawPasswordValue));
        Assert.False(user.RawPasswordValue.StartsWith(Constants.Security.EmptyPasswordPrefix, StringComparison.Ordinal));
        Assert.Equal(CurrentUserName, user.Username);
        Assert.True(user.IsApproved);
        Assert.Equal(new[] { Constants.Security.EditorGroupKey }, user.Groups.Select(group => group.Key));
        Assert.Equal(new[] { (Domain, sid) }, host.Logins(created.Key));
        using (var scope = host.App.Services.CreateScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<IAuditService>().GetItemsByEntityAsync(user.Id, 0, 20);
            Assert.Contains(audit.Items, item => item.AuditType == AuditType.New && item.EntityType == "User" && item.Comment!.Contains(sid));
        }

        // The search now reports the user, and creating a second one is a conflict.
        var after = await SearchAsync(administrator, "users", Environment.UserName);
        Assert.Equal(new ExistingAccount("user", created.Key, CurrentUserName, "active"), after.Existing[sid]);
        using (var again = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(again, HttpStatusCode.Conflict);
            Assert.Equal("Already has an account", problem.Title);
            Assert.Contains(CurrentUserName, problem.Detail);
        }

        // The SID alone opens the account, with name matching off, through IdentityServer.
        var signIn = AccountsTestHost.Account(sid, CurrentAccountName);
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(signIn, Domain), Strict));

        // Once retired, the account still holds the SID: the search says so, and a new user is refused.
        Assert.Equal(UserOperationStatus.Success, await host.Users.DeleteAsync(Constants.Security.SuperUserKey, created.Key));
        Assert.Equal(sid, (await host.Users.GetAsync(created.Key))!.Username);
        Assert.Equal("retired", (await SearchAsync(administrator, "users", Environment.UserName)).Existing[sid].State);
        using (var retired = await administrator.PostAsJsonAsync($"{Api}/users", new { sid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(retired, HttpStatusCode.Conflict);
            Assert.Equal("Retired account", problem.Title);
            Assert.Contains("enable that user in Users and give it back its username and email", problem.Detail);
        }
    }

    [Fact]
    public async Task A_member_created_from_the_directory_carries_the_SID_and_signs_in_by_it_alone()
    {
        var sid = CurrentSid;
        using (var scope = host.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMemberGroupService>().CreateAsync(new MemberGroup { Name = "Directory Readers" });
        using var administrator = await host.SignInAsAdministratorAsync();

        // Member types and groups are checked before anything is created.
        using (var unknownType = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberTypeAlias = "noSuchType", memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Unknown member type", (await ProblemAsync(unknownType, HttpStatusCode.BadRequest)).Title);
        using (var unknownGroup = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = new[] { "No Such Group" } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Unknown member group", (await ProblemAsync(unknownGroup, HttpStatusCode.BadRequest)).Title);

        var before = await SearchAsync(administrator, "members", Environment.UserName);
        var person = Assert.Single(before.Results, result => result.Sid == sid);
        Assert.DoesNotContain(sid, before.Existing.Keys);

        // The member type defaults to Accounts:MemberTypeAlias.
        using var response = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = new[] { "Directory Readers" } }, cancellationToken: TestContext.Current.CancellationToken);
        var created = await ReadAsync<CreatedAccount>(response, HttpStatusCode.Created);
        Assert.Equal(CurrentUserName, created.Username);
        Assert.Equal(person.DisplayName, created.Name);
        Assert.Equal((person.Email ?? $"{CurrentUserName}@localhost").ToLowerInvariant(), created.Email);
        var member = host.Members.GetById(created.Key)!;
        Assert.False(string.IsNullOrEmpty(member.RawPasswordValue));
        Assert.False(member.RawPasswordValue.StartsWith(Constants.Security.EmptyPasswordPrefix, StringComparison.Ordinal));
        Assert.True(member.IsApproved);
        Assert.Equal(AccountsTestHost.MemberTypeAlias, member.ContentTypeAlias);
        Assert.Equal(new[] { "Directory Readers" }, host.Members.GetAllRoles(member.Id));
        Assert.Equal(new[] { (DomainMembers, sid) }, host.Logins(created.Key));

        var after = await SearchAsync(administrator, "members", Environment.UserName);
        Assert.Equal(new ExistingAccount("member", created.Key, CurrentUserName, "active"), after.Existing[sid]);
        using (var again = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Already has an account", (await ProblemAsync(again, HttpStatusCode.Conflict)).Title);

        var signIn = AccountsTestHost.Account(sid, CurrentAccountName);
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(signIn, DomainMembers), Strict));

        using (var scope = host.App.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMemberEditingService>().DeleteAsync(created.Key, Constants.Security.SuperUserKey)).Success);
        Assert.Equal(sid, host.Members.GetById(created.Key)!.Username);
        using (var retired = await administrator.PostAsJsonAsync($"{Api}/members", new { sid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(retired, HttpStatusCode.Conflict);
            Assert.Equal("Retired account", problem.Title);
            Assert.Contains("enable that member in Members", problem.Detail);
        }
    }

    [Fact]
    public async Task With_matching_by_name_off_only_the_SID_opens_an_account()
    {
        const string sid = "S-1-5-21-900-901-902-1001";
        var person = AccountsTestHost.Account(sid, "DOMAIN\\Hand.Made", "hand.made@example.invalid");
        var refused = ProvisioningResult.NotFound(AccountProvisioning.NotLinkedReason);
        var user = await host.CreateUserAsync("hand.made", "hand.made@example.invalid", "Hand Made");
        var member = host.CreateMember("hand.made", "hand.made@example.invalid", "Hand Made");

        // A username and email that fit open nothing, and nothing gets linked.
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(person, DomainMembers), Strict));
        Assert.Empty(host.Logins(user.Key));
        Assert.Empty(host.Logins(member.Key));
        var stranger = AccountsTestHost.Account("S-1-5-21-900-901-902-9999", "DOMAIN\\No.One");
        Assert.Equal(refused, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(stranger, Domain), Strict));

        // With the default the same sign-ins claim the accounts, which the SID then opens either way.
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionUserAsync(person, Domain));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionMemberAsync(person, DomainMembers));
        Assert.Equal(new[] { (Domain, sid) }, host.Logins(user.Key));
        Assert.Equal(new[] { (DomainMembers, sid) }, host.Logins(member.Key));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
        Assert.Equal(ProvisioningResult.Ok, await host.ProvisionAsync(provisioning => provisioning.ProvisionMemberAsync(person, DomainMembers), Strict));

        // A retired SID is still refused as retired.
        Assert.Equal(UserOperationStatus.Success, await host.Users.DeleteAsync(Constants.Security.SuperUserKey, user.Key));
        Assert.Equal(ProvisioningResult.Unauthorized("This account has been retired."),
            await host.ProvisionAsync(provisioning => provisioning.ProvisionBackOfficeAsync(person, Domain), Strict));
    }

    [Fact]
    public async Task Requests_are_checked_before_the_directory_is_asked()
    {
        using var administrator = await host.SignInAsAdministratorAsync();

        using (var tooShort = await administrator.GetAsync($"{Api}/users/search?query=s", TestContext.Current.CancellationToken))
        {
            var problem = await ProblemAsync(tooShort, HttpStatusCode.BadRequest);
            Assert.Equal("The directory refused the search", problem.Title);
            Assert.Contains("at least 2 characters", problem.Detail);
        }
        using (var empty = await administrator.GetAsync($"{Api}/members/search", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var notASid = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = "DOMAIN\\Test.User", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("No person chosen", (await ProblemAsync(notASid, HttpStatusCode.BadRequest)).Title);
        using (var noGroup = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = CurrentSid, userGroupKeys = Array.Empty<Guid>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Choose a user group", (await ProblemAsync(noGroup, HttpStatusCode.BadRequest)).Title);
        using (var nobody = await administrator.PostAsJsonAsync($"{Api}/users", new { sid = "S-1-5-21-1-2-3-4242", userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal("Not in the directory", (await ProblemAsync(nobody, HttpStatusCode.NotFound)).Title);
    }

    [Fact]
    public async Task Only_people_with_access_to_the_section_may_search_or_create()
    {
        const string password = "Local-test-only-Directory91!";
        using (var anonymous = new HttpClient { BaseAddress = host.Address })
        using (var response = await anonymous.GetAsync($"{Api}/users/search?query={Environment.UserName}", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Editors have the Content and Media sections, but neither Users nor Members.
        var editor = await host.CreateUserAsync("directory.editor", "directory.editor@example.invalid", "Directory Editor");
        await host.AddPasswordAsync(editor, password);
        using var session = await host.SignInAsync("directory.editor", password);
        foreach (var path in new[] { $"{Api}/users/search?query={Environment.UserName}", $"{Api}/members/search?query={Environment.UserName}" })
        {
            using var response = await session.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var response = await session.PostAsJsonAsync($"{Api}/users", new { sid = CurrentSid, userGroupKeys = new[] { Constants.Security.EditorGroupKey } }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await session.PostAsJsonAsync($"{Api}/members", new { sid = CurrentSid, memberGroupNames = Array.Empty<string>() }, cancellationToken: TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<DirectorySearchResult> SearchAsync(HttpClient client, string kind, string query)
    {
        using var response = await client.GetAsync($"{Api}/{kind}/search?query={Uri.EscapeDataString(query)}");
        return await ReadAsync<DirectorySearchResult>(response, HttpStatusCode.OK);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private static async Task<(string Title, string Detail)> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var problem = await ReadAsync<JsonElement>(response, expected);
        return (problem.GetProperty("title").GetString()!, problem.GetProperty("detail").GetString()!);
    }
}

/// <summary>
/// The Add from directory package reads the <c>Directory</c> section; the host fills it from the
/// identity server, scheme and member type settings its sites already have.
/// </summary>
public sealed class DirectoryAccountSettingsMappingTests
{
    private const string Authority = "https://login.example/IdentityServer";
    private const string Key = "a-test-key-of-24-chars!!";

    [Fact]
    public void The_directory_settings_come_from_IdentityServer_and_Accounts()
    {
        using var services = Services(new()
        {
            ["IdentityServer:Authority"] = Authority,
            ["IdentityServer:DirectoryApiKey"] = Key,
            ["IdentityServer:AcceptAnyServerCertificate"] = "true",
            ["IdentityServer:Name"] = "Corp Login",
            ["Accounts:MemberTypeAlias"] = "staffMember",
        });
        var settings = services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value;

        Assert.Equal(Authority, settings.IdentityServerAuthority);
        Assert.Equal(Key, settings.IdentityServerApiKey);
        Assert.True(settings.IdentityServerAcceptAnyServerCertificate);
        Assert.Equal("Umbraco.CorpLogin", settings.BackOfficeLoginProvider);
        Assert.Equal("UmbracoMembers.CorpLogin", settings.MemberLoginProvider);
        Assert.Equal("staffMember", settings.MemberTypeAlias);
        Assert.Equal(AccountDirectorySource.IdentityServer, AccountDirectorySettings.Choose(settings));
        Assert.IsType<AppHostRetiredAccountPolicy>(services.GetRequiredService<IRetiredAccountPolicy>());
        Assert.IsType<AppHostSignInLinkPolicy>(services.GetRequiredService<ISignInLinkPolicy>());
    }

    [Fact]
    public void The_embedded_identity_server_leaves_the_choice_to_the_domain_and_the_local_accounts_setting()
    {
        using var services = Services(new());
        var settings = services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value;

        Assert.Equal("", settings.IdentityServerAuthority);
        Assert.Equal("Umbraco.IdentityServer", settings.BackOfficeLoginProvider);
        Assert.Equal("Member", settings.MemberTypeAlias);
        Assert.Equal(AccountDirectorySettings.IsOutsideDomain ? AccountDirectorySource.LocalAccounts : AccountDirectorySource.ActiveDirectory,
            AccountDirectorySettings.Choose(settings));
    }

    [Fact]
    public void A_value_in_the_Directory_section_wins()
    {
        using var services = Services(new()
        {
            ["IdentityServer:Authority"] = Authority,
            ["IdentityServer:DirectoryApiKey"] = Key,
            ["IdentityServer:Name"] = "Corp Login",
            ["Accounts:MemberTypeAlias"] = "staffMember",
            ["Directory:IdentityServerAuthority"] = "https://directory.example/IdentityServer",
            ["Directory:IdentityServerApiKey"] = "",
            ["Directory:MemberLoginProvider"] = "UmbracoMembers.Other",
            ["Directory:MemberTypeAlias"] = "contractor",
        });
        var settings = services.GetRequiredService<IOptions<AccountDirectorySettings>>().Value;

        Assert.Equal("https://directory.example/IdentityServer", settings.IdentityServerAuthority);
        Assert.Equal("", settings.IdentityServerApiKey);
        Assert.Equal("Umbraco.CorpLogin", settings.BackOfficeLoginProvider);
        Assert.Equal("UmbracoMembers.Other", settings.MemberLoginProvider);
        Assert.Equal("contractor", settings.MemberTypeAlias);
    }

    [Fact]
    public void The_retired_account_advice_follows_matching_by_name()
    {
        var matching = new AppHostRetiredAccountPolicy(Options.Create(new AccountsSettings()));
        Assert.Contains("enable that member in Members and give it back its username and email", matching.RestoreAdvice(DirectoryAccountKind.Member));
        var strict = new AppHostRetiredAccountPolicy(Options.Create(new AccountsSettings { LinkByUserNameOnFirstSignIn = false }));
        Assert.Contains($"change that user's username to one that starts with {AccountRetirement.RetiredUserNamePrefix}", strict.RestoreAdvice(DirectoryAccountKind.User));

        var links = new AppHostSignInLinkPolicy(Options.Create(new AccountsSettings { LinkByUserNameOnFirstSignIn = false }));
        Assert.Equal(new[] { LegacyWindowsLoginSchemes.BackOffice }, links.PreviousLoginProviders(DirectoryAccountKind.User));
        Assert.Equal(new[] { LegacyWindowsLoginSchemes.Member }, links.PreviousLoginProviders(DirectoryAccountKind.Member));
        Assert.False(links.FirstSignInLinksByName);
    }

    /// <summary>The three settings sections bound as the site binds them, with the host's mapping on top.</summary>
    private static ServiceProvider Services(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<IdentityServerSettings>(configuration.GetSection(IdentityServerSettings.SectionName));
        services.Configure<AccountsSettings>(configuration.GetSection(AccountsSettings.SectionName));
        services.Configure<AccountDirectorySettings>(configuration.GetSection(AccountDirectorySettings.SectionName));
        UmbracoAppHostExtensions.AddDirectoryAccountSettings(services, configuration);
        return services.BuildServiceProvider();
    }
}
