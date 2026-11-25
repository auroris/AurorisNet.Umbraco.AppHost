using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using UmbracoAppHost.Application;
using UmbracoAppHost.Authentication;
using UmbracoAppHost.Hosting;
using IdentityServer.Sso;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class MemberSignInHttpTests
{
    private const string prefix = "/wing";
    private const string scheme = "UmbracoMembers.TestIdentity";

    [Fact]
    [ResetPublishedContentServices]
    [OwnInstall]
    public async Task A_provisioned_member_and_backoffice_user_can_sign_in_in_either_order()
    {
        const string group = "Portal staff";
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
            // The first boot creates the configured member group, so this site has an install of its own.
            Settings = { ["IdentityServer:Enabled"] = "false", ["Accounts:BackOfficeMemberGroups:0"] = group },
        }, TestContext.Current.CancellationToken);
        var user = await site.CreateUserAsync("dual-login", "dual-login@example.invalid", "Dual login");
        var members = site.Services.GetRequiredService<IMemberService>();
        var member = members.GetByUsername(user.Username);
        Assert.NotNull(member);
        Assert.Equal(user.Email, member.Email);
        Assert.Equal(user.Name, member.Name);
        Assert.Contains(group, members.GetAllRoles(member.Id));
        const string backOfficePassword = "Local-test-only-Editor91!";
        const string memberPassword = "Local-test-only-Member91!";
        await site.SetPasswordAsync(user, backOfficePassword);
        await site.SetPasswordAsync(member, memberPassword);

        var contents = site.Services.GetRequiredService<IContentService>();
        var page = contents.GetById(ApplicationStarter.FormPageKey)!;
        var login = contents.Create("Member login", page.ParentId, "webPage");
        login.SetCultureName("Member login", "en");
        login.TemplateId = site.Services.GetRequiredService<IContentTypeService>().Get("webPage")!.DefaultTemplate!.Id;
        var blocks = new AppBlocks.Blocks.AppBlockValues();
        var signInBlock = blocks.Add(MemberSignInBlock.BlockAlias, new { heading = "Sign in to the portal" });
        blocks.Place(blocks.Add(AppBlocks.Blocks.AppBlockSchema.HtmlAlias, new { html = "<p>Before the form</p>" }));
        blocks.Place(signInBlock);
        blocks.Place(blocks.Add(AppBlocks.Blocks.AppBlockSchema.HtmlAlias, new { html = "<p>After the form</p>" }));
        login.SetValue("applicationBlocks", blocks.ToJson(), "en");
#pragma warning disable CS0618
        Assert.True(contents.Save(login).Success);
        Assert.True(contents.Publish(login, ["en"]).Success);
#pragma warning restore CS0618
        var rule = new PublicAccessRule { RuleType = Constants.Conventions.PublicAccess.MemberRoleRuleType, RuleValue = group };
        Assert.True(site.Services.GetRequiredService<IPublicAccessService>().Save(new PublicAccessEntry(page, login, login, [rule])).Success);

        foreach (var backOfficeFirst in new[] { true, false })
        {
            using var browser = site.CreateClient();
            if (backOfficeFirst) await SignInBackOffice();
            var html = await Page(browser, prefix + "/member-login/?sso-local=1");
            Assert.Contains("Sign in to the portal", html);
            Assert.True(html.IndexOf("Before the form", StringComparison.Ordinal) < html.IndexOf("app-login-form", StringComparison.Ordinal));
            Assert.True(html.IndexOf("app-login-form", StringComparison.Ordinal) < html.IndexOf("After the form", StringComparison.Ordinal));
            Assert.Equal(backOfficeFirst, html.Contains("Sign in with your member account to continue."));
            var form = ReadForm(html, form => form.Contains("class=\"app-login-form\"", StringComparison.Ordinal));
            form.Fields["loginModel.Username"] = member.Username;
            form.Fields["loginModel.Password"] = memberPassword;
            var missingToken = new Dictionary<string, string>(form.Fields);
            missingToken.Remove("__RequestVerificationToken");
            using var refused = await browser.PostAsync(form.Action, new FormUrlEncodedContent(missingToken), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using var signedIn = await browser.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            if (!backOfficeFirst) await SignInBackOffice();

            await site.AuthorizeManagementApiAsync(browser);
            var currentUser = await browser.GetFromJsonAsync<System.Text.Json.JsonElement>(prefix + "/umbraco/management/api/v1/user/current", TestContext.Current.CancellationToken);
            Assert.Equal(user.Key, currentUser.GetProperty("id").GetGuid());
            Assert.DoesNotContain("app-login-form", await Page(browser, prefix + "/member-login/"));
            var protectedPage = await Page(browser, prefix + "/orders-form/?id=4");
            Assert.Contains("Sign out", protectedPage);
            Assert.Contains("data-app-form", protectedPage);

            async Task SignInBackOffice()
            {
                using var response = await browser.PostAsJsonAsync(prefix + "/umbraco/management/api/v1/security/back-office/login",
                    new { username = user.Username, password = backOfficePassword }, TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
            }
        }

        login.SetValue("applicationBlocks", new AppBlocks.Blocks.AppBlockValues().ToJson(), "en");
#pragma warning disable CS0618
        Assert.True(contents.Save(login).Success);
        Assert.True(contents.Publish(login, ["en"]).Success);
#pragma warning restore CS0618
        using var visitor = site.CreateClient();
        Assert.DoesNotContain("app-login-form", await Page(visitor, prefix + "/member-login/"));
    }

    [Fact]
    [ResetPublishedContentServices]
    public async Task Member_sso_uses_native_callbacks_and_password_fallback_without_restarting_failed_or_two_factor_flows()
    {
        var provider = new ProviderProbe();
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
            StartSettings =
            {
                ["IdentityServer:Enabled"] = "true",
                ["IdentityServer:Name"] = "Test Identity",
                ["IdentityServer:Authority"] = "https://identity.example.invalid",
                ["IdentityServer:BackOfficeEnabled"] = "false",
                ["EmbeddedIdentityServer:Enabled"] = "false",
                ["Accounts:MembersManagedBy"] = "Umbraco",
            },
            ConfigureBuilder = builder => UseTestProvider(builder, provider),
            ConfigureApp = app => app.MapPost("/__tests/preview", async (IPreviewService preview, IUserService users) =>
                await preview.TryEnterPreviewAsync((await users.GetAsync(Constants.Security.SuperUserKey))!)),
        }, TestContext.Current.CancellationToken);
        var member = site.CreateMember("member-login", "member-login@example.invalid", "Member login");
        const string password = "Local-test-only-Member91!";
        await site.SetPasswordAsync(member, password);
        using var browser = site.CreateClient();
        const string destination = prefix + "/orders-form/?id=4&topic=member-test";

        using (var administrator = await site.SignInBackOfficeAsync(site.AdminUserName, site.AdminPassword))
        {
            using var start = await administrator.GetAsync(destination, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            using var providerRedirect = await administrator.GetAsync(start.Headers.Location, TestContext.Current.CancellationToken);
            Assert.Equal("identity.example.invalid", providerRedirect.Headers.Location!.Host);
            Assert.Contains("Sign in with your member account to continue.", await Page(administrator, destination + "&sso-local=1"));
            using var preview = await administrator.PostAsync(prefix + "/__tests/preview", null, TestContext.Current.CancellationToken);
            Assert.True(await preview.Content.ReadFromJsonAsync<bool>(TestContext.Current.CancellationToken));
            Assert.Contains("app-login-form", await Page(administrator, prefix + "/sign-in/"));
        }

        var automatic = await browser.GetAsync(destination, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, automatic.StatusCode);
        Assert.True(automatic.Headers.CacheControl?.NoStore);
        Assert.StartsWith(prefix + "/orders-form/?ufprt=", automatic.Headers.Location!.OriginalString);
        var challenge = await browser.GetAsync(automatic.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("identity.example.invalid", challenge.Headers.Location!.Host);
        Assert.Equal(destination, provider.Properties!.Items[SsoLoginUrls.MemberReturnUrlProperty]);
        var nativeCallback = provider.Properties.RedirectUri!;
        Assert.StartsWith(prefix + "/orders-form/?ufprt=", nativeCallback);

        var state = QueryHelpers.ParseQuery(challenge.Headers.Location.Query)["state"].ToString();
        var remoteFailure = await browser.GetAsync(QueryHelpers.AddQueryString(prefix + "/signin-oidc/member", new Dictionary<string, string?> {
            ["error"] = "temporarily_unavailable", ["error_description"] = "The test provider is unavailable.", ["state"] = state,
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, remoteFailure.StatusCode);
        var fallbackUrl = remoteFailure.Headers.Location!.OriginalString;
        Assert.StartsWith(destination + "&sso-local=1", fallbackUrl);
        var fallback = await Page(browser, fallbackUrl);
        Assert.Contains("loginModel.Username", fallback);
        Assert.Contains("Sign in with Test Identity", fallback);

        provider.Unavailable = true;
        var fresh = await browser.GetAsync(destination, TestContext.Current.CancellationToken);
        var unavailable = await browser.GetAsync(fresh.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, unavailable.StatusCode);
        Assert.StartsWith(destination + "&sso-local=1", unavailable.Headers.Location!.OriginalString);
        Assert.Contains("The identity server is unavailable", await Page(browser, unavailable.Headers.Location.OriginalString));
        provider.Unavailable = false;

        // An explicit retry is protected by antiforgery and cannot select a local cookie or backoffice scheme.
        var retry = ReadForm(fallback, form => form.Contains("name=\"provider\"", StringComparison.Ordinal));
        var missingToken = new Dictionary<string, string>(retry.Fields);
        missingToken.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(retry.Action, new FormUrlEncodedContent(missingToken), TestContext.Current.CancellationToken)).StatusCode);
        retry.Fields["provider"] = "Identity.Application";
        var wrongProvider = await browser.PostAsync(retry.Action, new FormUrlEncodedContent(retry.Fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, wrongProvider.StatusCode);
        Assert.Contains("sso-local=1", wrongProvider.Headers.Location!.OriginalString);
        retry.Fields["provider"] = scheme;
        var retried = await browser.PostAsync(retry.Action, new FormUrlEncodedContent(retry.Fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
        Assert.Equal("identity.example.invalid", retried.Headers.Location!.Host);

        // This native callback has no external ticket. Its provider error must show a password form, not restart SSO.
        var callbackError = await Page(browser, nativeCallback);
        Assert.Contains("Invalid response from the login provider", callbackError);
        Assert.Contains("loginModel.Username", callbackError);
        var twoFactor = await Page(browser, destination + "&test-two-factor=1");
        Assert.Contains("Security code for Test authenticator", twoFactor);
        Assert.DoesNotContain("loginModel.Username", twoFactor);
        Assert.DoesNotContain("Sign in with Test Identity", twoFactor);

        var login = ReadForm(callbackError, form => form.Contains("class=\"app-login-form\"", StringComparison.Ordinal));
        Assert.Equal(destination + "&sso-local=1", login.Action);
        Assert.DoesNotContain("ufprt", login.Action);
        login.Fields["loginModel.Username"] = member.Username;
        login.Fields["loginModel.Password"] = "Wrong-password91!";
        var refusedPassword = await browser.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refusedPassword.StatusCode);
        Assert.Contains("Invalid username or password", await refusedPassword.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        login.Fields["loginModel.Password"] = password;
        var signedIn = await browser.PostAsync(login.Action, new FormUrlEncodedContent(login.Fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(destination, signedIn.Headers.Location!.OriginalString);
        var memberCookieName = site.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name;
        Assert.EndsWith(site.Services.GetRequiredService<InstallationCookies>().Suffix, memberCookieName);
        var memberCookie = Assert.Single(signedIn.Headers.GetValues(HeaderNames.SetCookie).Select(value => SetCookieHeaderValue.Parse(value)),
            cookie => cookie.Name == memberCookieName && cookie.Value.Length > 0);
        Assert.Equal(prefix, memberCookie.Path.ToString());
        var memberPage = await Page(browser, destination);
        Assert.Contains("data-app-form", memberPage);
        var logout = ReadForm(memberPage, form => form.Contains("/application/account/logout", StringComparison.Ordinal));
        using var ended = await browser.PostAsync(logout.Action, new FormUrlEncodedContent(logout.Fields), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);
        Assert.Contains("app-login-form", await Page(browser, ended.Headers.Location!.OriginalString));
        var nextVisit = await Page(browser, prefix + "/sign-in/");
        Assert.Contains("Sign in with Test Identity", nextVisit);
        var explicitLogin = ReadForm(nextVisit, form => form.Contains("name=\"provider\"", StringComparison.Ordinal));
        using var explicitChallenge = await browser.PostAsync(explicitLogin.Action, new FormUrlEncodedContent(explicitLogin.Fields), TestContext.Current.CancellationToken);
        Assert.Equal("identity.example.invalid", explicitChallenge.Headers.Location!.Host);
    }

    /// <summary>
    /// Automatic sign-in goes through a URL the site protects and then reads back, so each site in a process has to use
    /// its own keys for it. It used to work on whichever site got there first, and leave every later one on the page.
    /// </summary>
    [Fact]
    [ResetPublishedContentServices]
    public async Task Automatic_sign_in_works_on_every_site_a_process_starts()
    {
        for (var start = 0; start < 2; start++)
        {
            await using var site = await TestSite.StartAsync(new TestSiteOptions
            {
                Application = typeof(Program).Assembly,
                StartSettings =
                {
                    ["IdentityServer:Enabled"] = "true",
                    ["IdentityServer:Name"] = "Test Identity",
                    ["IdentityServer:Authority"] = "https://identity.example.invalid",
                    ["IdentityServer:BackOfficeEnabled"] = "false",
                    ["EmbeddedIdentityServer:Enabled"] = "false",
                    ["Accounts:MembersManagedBy"] = "Umbraco",
                },
                ConfigureBuilder = builder => UseTestProvider(builder, new ProviderProbe()),
            }, TestContext.Current.CancellationToken);
            using var browser = site.CreateClient();
            var automatic = await browser.GetAsync("/orders-form/?id=4", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, automatic.StatusCode);
            var challenge = await browser.GetAsync(automatic.Headers.Location, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            Assert.Equal("identity.example.invalid", challenge.Headers.Location!.Host);
        }
    }

    /// <summary>Exercises the real OIDC challenge and callback handler without an external identity service.</summary>
    private static void UseTestProvider(WebApplicationBuilder builder, ProviderProbe provider)
    {
        builder.Services.PostConfigure<OpenIdConnectOptions>(scheme, options => {
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new() {
                Issuer = "https://identity.example.invalid",
                AuthorizationEndpoint = "https://identity.example.invalid/authorize",
                TokenEndpoint = "https://identity.example.invalid/token",
            });
            options.Events.OnRedirectToIdentityProvider = context => {
                provider.Properties = context.Properties;
                if (provider.Unavailable) throw new HttpRequestException("Test identity provider is unavailable.");
                return Task.CompletedTask;
            };
        });
        builder.Services.Configure<MvcOptions>(options => options.Filters.Add(new TwoFactorState()));
    }

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {html}");
        Assert.DoesNotContain("Could not render", html);
        return html;
    }

    private static (string Action, Dictionary<string, string> Fields) ReadForm(string html, Func<string, bool> matches)
    {
        var form = Regex.Matches(html, "<form\\b[^>]*>.*?</form>", RegexOptions.Singleline).Cast<Match>().Select(match => match.Value).Single(matches);
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(form, "<input\\b[^>]*>"))
        {
            var name = Regex.Match(input.Value, "\\bname=\"([^\"]*)\"");
            if (name.Success) fields[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
        }
        return (WebUtility.HtmlDecode(Regex.Match(form, "\\baction=\"([^\"]*)\"").Groups[1].Value), fields);
    }

    private sealed class ProviderProbe
    {
        public bool Unavailable { get; set; }
        public AuthenticationProperties? Properties { get; set; }
    }

    private sealed class TwoFactorState : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.HttpContext.Request.Query.ContainsKey("test-two-factor") && context.Result is ViewResult view)
                view.ViewData!.SetTwoFactorProviderNames(["Test authenticator"]);
        }
        public void OnResultExecuted(ResultExecutedContext context) { }
    }
}
