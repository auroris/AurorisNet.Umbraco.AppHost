using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using BreakGlass;
using IdentityServer.Sso;
using File = System.IO.File;
using TestTwoFactorProvider = UmbracoAppHost.Tests.BackOfficeSsoHttpTests.TestTwoFactorProvider;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
[OwnInstall]
public sealed class ReferenceDevelopmentLoginTests
{
    private const string SavedUsername = "administrator";
    private const string SavedEmail = "previous-admin@example.invalid";
    private const string InstalledPassword = "Local-test-only-Reference91!";
    private const string OtherPassword = "An unrelated nonempty password";
    private const string TwoFactorSecret = "existing-test-authenticator-secret";
    private const string Api = "/umbraco/management/api/v1/security/back-office";

    [Fact]
    [ResetBackOfficePath]
    public async Task Debug_recovers_locked_two_factor_account_without_changing_credentials_and_normal_flows_still_enforce_them()
    {
        var root = Directory.CreateTempSubdirectory("reference-login-").FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(root, "appsettings.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), Path.Combine(root, "appsettings.Development.json"));
        string? originalHash;
        await using (var app = await StartApp(root, "Production"))
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
                var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
                var installedEmail = app.Configuration["Umbraco:CMS:Unattended:UnattendedUserEmail"]!;
                Assert.Equal(installedEmail, administrator.UserName);
                Assert.Equal(installedEmail, administrator.Email);
                Assert.Equal(HttpStatusCode.OK, await Login(app, installedEmail, InstalledPassword));
                originalHash = administrator.PasswordHash;
                // A site-managed identity must survive subsequent starts and recovery.
                administrator.UserName = SavedUsername;
                administrator.Email = SavedEmail;
                Assert.True((await users.UpdateAsync(administrator)).Succeeded);
                await scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>().SaveAsync(new TwoFactorLogin
                {
                    UserOrMemberKey = Constants.Security.SuperUserKey, ProviderName = TestTwoFactorProvider.Name,
                    Secret = TwoFactorSecret, Confirmed = true,
                });
                // Use the real native endpoint to create an account lockout before recovery.
                for (var attempt = 0; attempt < 6; attempt++)
                    Assert.Contains(await Login(app, SavedUsername, OtherPassword),
                        new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
                var locked = await AccountState(app);
                Assert.True(locked.Locked);
                Assert.True(locked.Failures > 0);
                Assert.Equal(TwoFactorSecret, locked.TwoFactorSecret);
            }
            finally { await app.StopAsync(TestContext.Current.CancellationToken); }
        }

        await using (var app = await StartApp(root, "Development", enableRecovery: true))
        {
            try
            {
                Assert.True(app.Services.GetRequiredService<IOptions<HostingSettings>>().Value.Debug);
                using var accountScope = app.Services.CreateScope();
                var administrator = await accountScope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>()
                    .FindByIdAsync(Constants.Security.SuperUserKey.ToString());
                Assert.NotNull(administrator);
                Assert.Equal(SavedUsername, administrator.UserName);
                Assert.Equal(SavedEmail, administrator.Email);
                Assert.Equal(originalHash, administrator.PasswordHash);
                var before = await AccountState(app);
                Assert.True(before.Locked);
                Assert.Equal(TestTwoFactorProvider.Name, before.TwoFactorProviders);
                // A refused attempt leaves the lockout alone; only a recovery sign-in lifts it.
                await AssertRefused(app, SavedUsername, "");
                Assert.Equal(before, await AccountState(app));
                using (var browser = Browser(app))
                {
                    var loginStarted = DateTime.UtcNow;
                    using var login = await browser.PostAsJsonAsync(Api + "/login", new { username = SavedUsername, password = OtherPassword }, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                    Assert.True(login.SetsBackOfficeLoginCookie(app.Services));
                    await CompleteAuthorization(browser);
                    var unlocked = await AccountState(app);
                    Assert.False(unlocked.Locked);
                    Assert.Equal(0, unlocked.Failures);
                    Assert.Equal(before.Hash, unlocked.Hash);
                    Assert.Equal(before.TwoFactorSecret, unlocked.TwoFactorSecret);
                    Assert.Equal(before.TwoFactorProviders, unlocked.TwoFactorProviders);
                    using (var scope = app.Services.CreateScope())
                    {
                        var recovered = (await scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>()
                            .FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
                        Assert.NotEqual(administrator.LastLoginDate, recovered.LastLoginDate);
                        Assert.InRange(Assert.IsType<DateTime>(recovered.LastLoginDate),
                            loginStarted.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
                    }
                    await AssertRefused(app, "not-a-user", OtherPassword);
                    await AssertRefused(app, "", OtherPassword);
                    await AssertRefused(app, SavedUsername, "");
                    Assert.Equal(unlocked, await AccountState(app));

                    // Recovery is usable for the actual native account repair operation.
                    using var unlock = await browser.PostAsJsonAsync("/umbraco/management/api/v1/user/unlock", new { userIds = new[] { new { id = Constants.Security.SuperUserKey } } }, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.True(unlock.IsSuccessStatusCode, await unlock.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                    Assert.False((await AccountState(app)).Locked);
                }
                await LockAccount(app);
            }
            finally { await app.StopAsync(TestContext.Current.CancellationToken); }
        }

        // The reference website selects the Debug login flow in Development. Recovery still requires opt-in.
        await using (var app = await StartApp(root, "Development"))
        {
            try
            {
                Assert.True(app.Services.GetRequiredService<IOptions<HostingSettings>>().Value.Debug);
                Assert.Equal(BackOfficeLoginFlow.Debug, app.Services.GetRequiredService<IOptions<BackOfficeLoginSettings>>()
                    .Value.GetEffectiveFlow(true));
                Assert.False(app.Services.GetRequiredService<IOptionsMonitor<BreakGlassSettings>>().CurrentValue.DebugSignIn);
                await AssertNativeSecurity(app);
                // Restore a lockout so the following Production startup must enforce it too.
                await LockAccount(app);
            }
            finally { await app.StopAsync(TestContext.Current.CancellationToken); }
        }

        await using (var app = await StartApp(root, "Production"))
        {
            try
            {
                Assert.False(app.Services.GetRequiredService<IOptions<HostingSettings>>().Value.Debug);
                await AssertNativeSecurity(app);
                var final = await AccountState(app);
                Assert.Equal(originalHash, final.Hash);
                Assert.Equal(TwoFactorSecret, final.TwoFactorSecret);
                Assert.Equal(TestTwoFactorProvider.Name, final.TwoFactorProviders);
            }
            finally { await app.StopAsync(TestContext.Current.CancellationToken); }
        }
    }

    private static async Task<WebApplication> StartApp(string root, string environment, bool enableRecovery = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root, ApplicationName = typeof(Program).Assembly.GetName().Name, EnvironmentName = environment,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceRoot"] = Path.Combine(root, "instance"),
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={Path.Combine(root, "umbraco.db")};Pooling=False",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = InstalledPassword,
            ["uSync:Settings:ExportOnSave"] = "None",
            [TestSite.ConsoleLogLevelSetting] = "Error",
        });
        if (enableRecovery)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [BreakGlassSettings.DebugSignInKey] = "true" });
        builder.AddUmbracoAppHost();
        UmbracoTestSupport.UseSingleServer(builder.Services);
        builder.Services.AddSingleton<TestTwoFactorProvider>();
        builder.Services.AddSingleton<ITwoFactorProvider>(services => services.GetRequiredService<TestTwoFactorProvider>());
        builder.Services.Configure<BackOfficeIdentityOptions>(options =>
            options.Tokens.ProviderMap[TestTwoFactorProvider.Name] = new TokenProviderDescriptor(typeof(TestTwoFactorProvider)));
        var app = builder.Build();
        try
        {
            await app.UseUmbracoAppHostAsync();
            await app.StartAsync();
            await UmbracoTestSupport.WaitForInstallAsync(app.Services, TimeSpan.FromMinutes(3));
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private static async Task<HttpStatusCode> Login(WebApplication app, string username, string password)
    {
        using var client = Browser(app);
        using var response = await client.PostAsJsonAsync(Api + "/login", new { username, password });
        return response.StatusCode;
    }

    private static HttpClient Browser(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static async Task AssertRefused(WebApplication app, string username, string password)
    {
        using var browser = Browser(app);
        using var response = await browser.PostAsJsonAsync(Api + "/login", new { username, password });
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.False(response.SetsBackOfficeLoginCookie(app.Services));
    }

    private static async Task AssertNativeSecurity(WebApplication app)
    {
        var locked = await AccountState(app);
        Assert.True(locked.Locked);
        Assert.Equal(HttpStatusCode.Forbidden, await Login(app, SavedUsername, InstalledPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(app, SavedUsername, OtherPassword));
        Assert.Equal(locked, await AccountState(app));

        // Only the fixture unlocks the account, so the independent MFA check can be observed.
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
            var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
            Assert.True((await users.SetLockoutEndDateAsync(administrator, DateTimeOffset.UtcNow.AddMinutes(-1))).Succeeded);
            Assert.True((await users.ResetAccessFailedCountAsync(administrator)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(app, SavedUsername, OtherPassword));
        using var browser = Browser(app);
        using (var login = await browser.PostAsJsonAsync(Api + "/login", new { username = SavedUsername, password = InstalledPassword }))
        {
            Assert.Equal(HttpStatusCode.PaymentRequired, login.StatusCode);
            Assert.Contains(TestTwoFactorProvider.Name, await login.Content.ReadAsStringAsync());
            Assert.False(login.SetsBackOfficeLoginCookie(app.Services));
        }
        using var verification = await browser.PostAsJsonAsync(Api + "/verify-2fa", new
        {
            provider = TestTwoFactorProvider.Name, code = TestTwoFactorProvider.Code, isPersistent = false, rememberClient = false,
        });
        Assert.True(verification.IsSuccessStatusCode, await verification.Content.ReadAsStringAsync());
        await CompleteAuthorization(browser);
    }

    private static async Task CompleteAuthorization(HttpClient browser)
    {
        var callback = new Uri(browser.BaseAddress!, "/umbraco/oauth_complete").ToString();
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var continuation = QueryHelpers.AddQueryString(Api + "/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = Constants.OAuthClientIds.BackOffice, ["redirect_uri"] = callback, ["response_type"] = "code",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["state"] = "debug-recovery-state",
        });
        using var authorize = await browser.GetAsync(continuation);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var location = new Uri(browser.BaseAddress!, authorize.Headers.Location!);
        Assert.StartsWith(callback + "?", location.ToString());
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("debug-recovery-state", query["state"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code"]));
        using var token = await browser.PostAsync(Api + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = Constants.OAuthClientIds.BackOffice,
            ["redirect_uri"] = callback, ["code"] = query["code"].ToString(), ["code_verifier"] = verifier,
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", document.RootElement.GetProperty("access_token").GetString());
        using var current = new HttpRequestMessage(HttpMethod.Get, "/umbraco/management/api/v1/user/current");
        using var user = await browser.SendAsync(current);
        var userBody = await user.Content.ReadAsStringAsync();
        Assert.True(user.IsSuccessStatusCode, userBody);
        Assert.Contains(SavedEmail, userBody, StringComparison.OrdinalIgnoreCase);
        using var users = await browser.GetAsync("/umbraco/management/api/v1/user");
        Assert.True(users.IsSuccessStatusCode, await users.Content.ReadAsStringAsync());
    }

    private static async Task LockAccount(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
        Assert.True((await users.SetLockoutEndDateAsync(administrator, DateTimeOffset.UtcNow.AddDays(1))).Succeeded);
    }

    private static async Task<Credentials> AccountState(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackOfficeUserManager>();
        var administrator = (await users.FindByIdAsync(Constants.Security.SuperUserKey.ToString()))!;
        var factors = scope.ServiceProvider.GetRequiredService<ITwoFactorLoginService>();
        return new Credentials(administrator.PasswordHash, administrator.SecurityStamp,
            await users.IsLockedOutAsync(administrator), administrator.LockoutEnd, administrator.AccessFailedCount,
            await factors.GetSecretForUserAndProviderAsync(Constants.Security.SuperUserKey, TestTwoFactorProvider.Name),
            string.Join(",", await factors.GetEnabledTwoFactorProviderNamesAsync(Constants.Security.SuperUserKey)));
    }

    private sealed record Credentials(string? Hash, string? Stamp, bool Locked, DateTimeOffset? LockoutEnd, int Failures,
        string? TwoFactorSecret, string TwoFactorProviders);
}
