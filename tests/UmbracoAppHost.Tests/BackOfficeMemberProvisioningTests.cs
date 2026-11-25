using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
[OwnInstall]
public sealed class BackOfficeMemberProvisioningTests
{
    [Fact]
    public async Task New_users_receive_the_instance_groups_once_and_ambiguous_matches_roll_back()
    {
        const string first = "Portal staff";
        const string second = "Reviewers";
        const string seedEmail = "configured@example.invalid";
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            UseInstallSnapshot = false,
            ConfigureBuilder = builder =>
            {
                builder.Services.AddSingleton<IPasswordValidator<MemberIdentityUser>, RejectOneMemberPassword>();
                var root = Directory.CreateDirectory(builder.Configuration["InstanceRoot"]!).FullName;
                System.IO.File.WriteAllText(Path.Combine(root, "instance.json"), JsonSerializer.Serialize(new
                {
                    Accounts = new
                    {
                        BackOfficeMemberGroups = new[] { first, second },
                        EnsureBackOfficeUsers = new[] { new { Email = seedEmail, UserGroups = new[] { "editor" } } },
                    },
                    Umbraco = new { CMS = new { Unattended = new
                    {
                        InstallUnattended = true, UnattendedUserEmail = "admin@localhost.local", UnattendedUserName = "Administrator",
                    } } },
                }));
            },
        }, TestContext.Current.CancellationToken);
        using var scope = site.CreateScope();
        var members = scope.ServiceProvider.GetRequiredService<IMemberService>();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        foreach (var username in new[] { site.AdminUserName, seedEmail })
        {
            var member = members.GetByUsername(username);
            Assert.True(member is not null, $"Expected a member for '{username}'.");
            Assert.Equal(users.GetByUsername(username)!.Email, member.Email);
            Assert.Equal([first, second], members.GetAllRoles(member.Id).Order());
        }

        var existing = site.CreateMember("old-login", "member@example.invalid", "Existing member", "Other");
        await site.SetPasswordAsync(existing, "Local-test-existing91!");
        var passwordHash = members.GetById(existing.Id)!.RawPasswordValue;
        var user = await site.CreateUserAsync("new-login", existing.Email, "Backoffice name");
        var reused = members.GetByEmail(user.Email)!;
        Assert.Equal(existing.Key, reused.Key);
        Assert.Equal("Existing member", reused.Name);
        Assert.Equal(passwordHash, reused.RawPasswordValue);
        Assert.Null(members.GetByUsername(user.Username));
        Assert.Equal(["Other", first, second], members.GetAllRoles(existing.Id).Order());
        members.DissociateRole(existing.Id, second);
        user.Name = "Edited";
        users.Save(user);
        Assert.DoesNotContain(second, members.GetAllRoles(existing.Id));

        var byName = site.CreateMember("shared-name", "original@example.invalid", "Username match");
        var byEmail = site.CreateMember("different-name", "different@example.invalid", "Email match");
        var refused = await users.CreateAsync(Constants.Security.SuperUserKey, new UserCreateModel
        {
            UserName = byName.Username, Email = byEmail.Email, Name = "Conflict", UserGroupKeys = new HashSet<Guid> { Constants.Security.EditorGroupKey },
        }, approveUser: true);
        Assert.False(refused.Success);
        Assert.Contains("match different members", refused.Result.Error?.ErrorMessage);
        Assert.Null(users.GetByUsername(byName.Username));
        Assert.Empty(members.GetAllRoles(byName.Id));
        Assert.Empty(members.GetAllRoles(byEmail.Id));

        await site.CreateUserAsync(byName.Username, "new@example.invalid", "Username match");
        Assert.Equal([first, second], members.GetAllRoles(byName.Id).Order());
        Assert.Null(members.GetByEmail("new@example.invalid"));

        var direct = users.CreateUserWithIdentity("direct", "direct@example.invalid");
        Assert.Equal([first, second], members.GetAllRoles(members.GetByUsername(direct.Username)!.Id).Order());

        var sharedEmail = site.CreateMember("first-inbox", "shared@example.invalid", "First");
        var other = site.CreateMember("second-inbox", sharedEmail.Email, "Second");
        var conflict = Assert.Throws<InvalidOperationException>(() => users.CreateUserWithIdentity("shared-inbox", sharedEmail.Email));
        Assert.Contains("match different members", conflict.Message);
        Assert.Null(users.GetByUsername("shared-inbox"));
        Assert.Empty(members.GetAllRoles(sharedEmail.Id));
        Assert.Empty(members.GetAllRoles(other.Id));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            site.CreateUserAsync("rejected-member", "rejected@example.invalid", "Rejected"));
        Assert.Contains("password", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(users.GetByUsername("rejected-member"));
        Assert.Null(members.GetByUsername("rejected-member"));
    }

    private sealed class RejectOneMemberPassword : IPasswordValidator<MemberIdentityUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<MemberIdentityUser> manager, MemberIdentityUser user, string? password)
            => Task.FromResult(user.UserName == "rejected-member"
                ? IdentityResult.Failed(new IdentityError { Description = "Rejected test password." })
                : IdentityResult.Success);
    }
}
