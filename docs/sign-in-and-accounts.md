# Sign-in and accounts

A site signs in backoffice users and members with their Windows accounts through an embedded
identity server, and falls back to Umbraco's password form. Three packages do the work. This
page describes the defaults the host gives them and what it adds: the administrator's identity,
the member sign-in view, the account menu, and who a protected page admits.

| Package | Provides | Registered by |
|---|---|---|
| [`AurorisNet.Umbraco.BreakGlass`](https://github.com/auroris/AurorisNet.Umbraco.BreakGlass) | The configured administrator password and debug sign-in | Its own composer |
| [`AurorisNet.Umbraco.IdentityServer.SSO`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO) | The OpenID Connect provider, the embedded identity server, login flows, configured accounts and account retirement | `AddUmbracoAppHost`, which calls `AddSingleSignOn` |
| [`AurorisNet.Umbraco.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory) | The **Add from directory** button in Users and Members | Its own composer |

Each package's README lists every setting and describes its behavior. The defaults below come
from `src/UmbracoAppHost/Hosting/defaults.json` and replace the packages' own. Restart the site
after changing a `BackOfficeLogin`, `IdentityServer` or `EmbeddedIdentityServer` setting.

Each installation has its own sign-in cookies. For sites sharing a hostname, see
[Running several installations on one hostname](hosting-under-a-sub-path.md#running-several-installations-on-one-hostname).

## The built-in administrator

Local backoffice sign-in uses a username. The host sets `Umbraco:CMS:Security:UsernameIsEmail`
to `false`, so Users shows separate username and email fields.

| `BreakGlass` setting | This host's default | Effect |
|---|---|---|
| `AdminUserName` | `admin` | The username of the built-in administrator (user ID `-1`), applied at startup and whenever the configuration reloads. An empty string leaves the username to Users. |
| `AdminEmail` | `admin@localhost.local` | The administrator's email, applied the same way. An empty string leaves it to Users. |
| `AdminPassword` | empty | A password to apply. Empty leaves the password, lockout state and two-factor enrolment as they are. |
| `DebugSignIn` | follows the login flow | See [Debug sign-in](#debug-sign-in). |

A new instance gets a random administrator password, stored in its `instance.json` under
`Umbraco:CMS:Unattended:UnattendedUserPassword`. Sign in as `admin` with that password.

### Choose or reset the administrator password

1. Set `BreakGlass:AdminPassword` in the website's `appsettings.json`, which is
   `app/appsettings.json` in a site folder:

   ```json
   "BreakGlass": {
     "AdminPassword": "A new password that meets the site's rules"
   }
   ```

2. Save the file. A running site applies it when the file reloads, without an IIS restart, and
   a stopped site applies it at its next start. The password is set for the original
   administrator even if that account was renamed. The account is unlocked, its failed
   attempts are cleared and its two-factor enrolment is removed.
3. Sign in, then remove the setting and save. While the password stays configured, every reload
   applies it again and removes two-factor enrolment.

The password must meet the site's Umbraco password rules. A rejected password is logged, without
the password itself, and the account keeps its password and lockout state.

**The configuration is a credential.** Whoever can edit the site's settings can set the
administrator's password. See the package's
[README](https://github.com/auroris/AurorisNet.Umbraco.BreakGlass) for environment variables and
the security notes.

### Older setting names

`UmbracoAppHost:AdminPassword`, `UmbracoAppHost:AdminUserName` and `UmbracoAppHost:AdminEmail`
still work, and the reference website's `appsettings.json` uses them. When both names are set,
the configuration source with the higher precedence decides, and within one source the
`BreakGlass` name does. A site's `appsettings.json` with the older names therefore still
replaces the library's defaults.

## Backoffice login flow

```json
"BackOfficeLogin": {
  "DefaultFlow": "Auto",
  "DebugFlow": "Debug"
}
```

| Flow | The login screen |
|---|---|
| `Auto` | Redirects to the identity server when its backoffice provider can sign people in, and shows the password form otherwise. |
| `Form` | Shows the password form with the provider button. |
| `IdentityServer` | Always redirects. Startup stops when the provider is off. |
| `Debug` | Shows the password form and never redirects. Outside debug mode it acts as `Form`. |

`DefaultFlow` applies outside debug mode. `DebugFlow` applies while `Umbraco:CMS:Hosting:Debug`
is `true`; set it to `Form` to check real passwords during development, or to `null` to use
`DefaultFlow`. A failed single sign-on attempt returns to the password form with the
authorization request intact, so a valid password completes the sign-in.

## Debug sign-in

> **Warning.** While debug sign-in applies, anyone who can reach the backoffice login and knows
> the username of an enabled account can sign in as that account with any nonempty password. It
> skips the password, lockout and two-factor checks.

Debug sign-in is `BreakGlass:DebugSignIn` in the BreakGlass package and works only while
`Umbraco:CMS:Hosting:Debug` is `true`. In this host, a site that leaves `BreakGlass:DebugSignIn`
out gets it from the login flow: debug sign-in is on when the flow selected for debug mode is
`Debug`. With the library's defaults, `DebugFlow` is `Debug`, so **turning on Umbraco's debug mode
alone turns on debug sign-in**. Set `BreakGlass:DebugSignIn` to `false`, or `DebugFlow` to `Form`,
on a site that needs debug mode without it.

The reference website's `appsettings.Development.json` turns debug mode on, so the **Website**
launch profile accepts `admin` with any nonempty password. A site created by `new-site.ps1` has
debug mode off in its `instance.json`.

To get into a deployed site whose accounts are locked out or have lost their two-factor device:

1. Set `Umbraco:CMS:Hosting:Debug` to `true` in `app/appsettings.json`. Keep
   `BackOfficeLogin:DebugFlow` at `Debug`, or set `BreakGlass:DebugSignIn` to `true`. Restart
   the site. The hosting environment name plays no part: the same settings work in Production.
2. Sign in to an existing account with any nonempty password. The sign-in lifts the account's
   lockout and leaves its password and two-factor enrolment as they are.
3. Repair the account in **Users**.
4. Restore the settings and restart.

Prefer [the configured password](#choose-or-reset-the-administrator-password) when the built-in
administrator is the account to recover.

## Single sign-on defaults

The library turns the provider on against the embedded identity server.

| `IdentityServer` setting | This host's default |
|---|---|
| `Enabled`, `BackOfficeEnabled`, `MemberEnabled` | `true` |
| `Name` | `Domain`, which gives the schemes `Umbraco.Domain` and `UmbracoMembers.Domain`. Stored account links name the scheme, so keep the name once accounts are linked. |
| `Authority` | Empty: the site signs in through its embedded identity server. |
| `ClientId`, `ClientSecret` | `umbraco-backoffice`, no secret. The embedded server admits the site's own client without registration. |
| `BackOfficeAutoLink` | `AutoLinkExternalAccount` `false`, `AllowManualLinking` `true`, no default groups. |
| `MemberAutoLink` | `AutoLinkExternalAccount` `true`, `DefaultIsApproved` `true`, `DefaultMemberTypeAlias` `Member`, no default groups, `ExternalOnly` `false`. |
| `CallbackPath` | `/signin-oidc` |
| `RequireHttpsMetadata`, `AcceptAnyServerCertificate`, `DenyLocalLogin` | `true`, `false`, `false` |
| `DirectoryApiKey` | Empty. See [Add from directory](#add-from-directory). |

With these defaults a member account is created at a person's first sign-in. A backoffice user
is not: create the user and link the identity first, with **Add from directory** or from the
user's profile after a password sign-in, or set `BackOfficeAutoLink:AutoLinkExternalAccount` to
`true`.

To sign in through an external identity server, set `IdentityServer:Authority` and the client
settings in the site's `instance.json`, and register the callbacks `/signin-oidc/backoffice` and
`/signin-oidc/member` at that server. Set `IdentityServer:Enabled` to `false` to offer password
sign-in alone.

| `EmbeddedIdentityServer` setting | This host's default |
|---|---|
| `Enabled` | `true` |
| `Clients`, `Groups` | Empty: no other applications sign in here, and tokens carry no roles. |
| `PersistKeys` | `true` |
| `DataPath` | Empty, which the host resolves to `data/keys/identity-server/` in the instance folder. `reset.ps1` deletes it with the rest of `data/`. |
| `AccessTokenLifetime`, `IdentityTokenLifetime`, `AuthorizationCodeLifetime` | `01:00:00`, `01:00:00`, `00:05:00` |

Under IIS, enable **Windows Authentication** and **Anonymous Authentication** for the
application and run the package's helper script, as
[Deployment](deployment.md#host-a-site-in-iis) describes. With Anonymous Authentication alone,
the login screens leave the embedded identity server out and people sign in with a password or
through an external identity server. The package's
[embedded server reference](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO/blob/main/docs/EMBEDDED-SERVER.md)
covers clients, claims, keys and troubleshooting.

## Accounts

The `Accounts` section is shared: the single sign-on package reads the account keys, and the
host reads the two report keys.

| `Accounts` setting | This host's default | Effect |
|---|---|---|
| `EnsureMemberGroups` | `Report Users`, `Users` | Member groups created when missing. |
| `EnsureUserGroups`, `EnsureBackOfficeUsers`, `EnsureMembers` | empty | User groups and accounts created when missing. |
| `MemberTypeAlias` | `Member` | The type of members created from `EnsureMembers` or from the directory. |
| `RetireDeletedUsers`, `RetireDeletedMembers` | `true` | Delete retires an account that has signed in. See [Retiring accounts](#retiring-accounts). |
| `SeedCurrentWindowsUserAsAdministrator` | `false` | Development: `true` creates `<your Windows user>@localhost` as an administrator on the first run. |
| `ReportAdministratorUserGroups` | `admin`, `interactiveReportAdministrators` | Backoffice groups whose users administer Interactive Reports. See [Interactive Reports](interactive-reports.md#who-may-do-what). |
| `AllowAnonymousReportBlocks` | `false` | `true` lets visitors without an account read report blocks on pages without Public Access. See [Interactive Reports](interactive-reports.md#anonymous-visitors). |

The host adds two startup steps to the package's configured accounts:

- It creates the configured groups before every uSync import, the first boot's included. A
  group picker in imported content drops a group that does not exist yet, which would leave a
  data source without its write group.
- It creates the user group **Interactive Report Administrators**
  (`interactiveReportAdministrators`) with access to the Content section, before the configured
  users are created, so a configured user can join it on the first run.

Accounts that the configured lists or the directory create get a random password. An
administrator sets a usable one in **Users** or **Members** when the person needs the password
form. Single sign-on leaves existing passwords alone.

## Member sign-in

The sign-in view is `Views/Partials/App/SignIn.cshtml`, used by the **Site sign in** template
and by both themes' sign-in layouts.

- A first visit to the view redirects to the identity server when `IdentityServer:Enabled` and
  `MemberEnabled` are `true`. Public pages stay public.
- A failed attempt returns to Umbraco's member password form on the same page, with the reason
  and a button to try single sign-on again. The original page and its query parameters are kept.
- `?sso-local=1` on a sign-in page shows the password form at once.
- With no identity server enabled for members, the view shows the password form and the buttons
  of any other configured provider.

The application theme's account menu shows the signed-in name with **Member account** or
**Backoffice account**, and **Sign out**. Sign-out posts to `/application/account/logout` with
an antiforgery token and ends the member session and the backoffice session together, so the
next request cannot switch from one account to the other. A menu entry can offer the same
sign-out with the `{{user.signout}}` token; see
[Menus](application-blocks.md#menus).

## Who a protected page admits

Umbraco's Public Access decides first: a page it protects admits the members it names. The host
adds one rule, `EditorPageAdmission`, registered with AppBlocks as an `IProtectedPageAdmission`:

- A backoffice user who may edit the page is admitted without a member account. "May edit" is
  Umbraco's **Update** permission on the page, through the user's groups and within their start
  nodes, for an approved account that is not locked out.
- Any other backoffice user gets the page's login page and signs in there with a member account
  the rule names.
- When a member session exists, it decides. A member Umbraco refuses is not replaced by the
  backoffice account, so an editor signs the member out first.

The page and every block endpoint on it follow the same answer. On a page without Public
Access, a signed-in backoffice user of any group reaches the blocks without a member account,
and whatever is behind each block decides. The sample Orders form accepts signed-in members and
backoffice users in **Administrators**.

Views read the visitor through `SiteVisitor`, which `_ViewImports.cshtml` injects as `Visitor`.
`await Visitor.ResolveAsync()` resolves the member, the backoffice user or nobody once per
request, and `IsSignedIn`, `IsMember`, `IsBackOfficeUser`, `Name`, `UserName` and `Email` describe
the result.

## Add from directory

The Users and Members sections have an **Add from directory** button that creates an account
for a person found in the directory and links it to their Windows SID. The package's
[README](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.Directory) describes the
dialog, the account rules, the management API and the sources.

The package reads the `Directory` section. The host fills these settings from the sign-in
settings when the `Directory` section leaves them out, so the dialog follows the sign-in:

| `Directory` setting | Filled from |
|---|---|
| `IdentityServerAuthority` | `IdentityServer:Authority` |
| `IdentityServerApiKey` | `IdentityServer:DirectoryApiKey` |
| `IdentityServerAcceptAnyServerCertificate` | `IdentityServer:AcceptAnyServerCertificate` |
| `BackOfficeLoginProvider`, `MemberLoginProvider` | The sign-in's scheme names, `Umbraco.Domain` and `UmbracoMembers.Domain` with the default `Name` |
| `MemberTypeAlias` | `Accounts:MemberTypeAlias` |

A value set in the `Directory` section takes precedence. The library's other defaults are an
empty `UserRoot` and `UserRootGlobal`, which search the whole domain, `GlobalSearchMinLength` 2,
`GlobalSearchSizeLimitBuffer` 20, `DefaultTake` 20 and `MaxTake` 50.

With the default `Directory:Source` of `Auto`, the people come from:

- **Active Directory**, queried by the site as its process account, when the site signs in
  through its embedded identity server on a computer in a domain. Under IIS the application
  pool identity needs read access to the directory; `ApplicationPoolIdentity` has it on a
  domain-joined server.
- **The external identity server's directory API**, when `IdentityServer:Authority` is set. The
  site sends `IdentityServer:DirectoryApiKey` in the `X-Api-Key` header, or with no key
  authenticates as its own Windows identity, which the server must admit.
- **The computer's local accounts**, on a development machine outside a domain.

The host also tells the dialog about its account policy. An account whose username is a SID or
starts with `retired-` is shown as retired, with advice on bringing the person back. Links
stored under the earlier Windows login schemes still count as the person's account. When
`Accounts:LinkByUserNameOnFirstSignIn` is `true`, the package's default, the dialog says that a
person can claim an unlinked account of the same name by signing in.

Application code can search the directory too, through `IDirectoryLookup` from the
[`AurorisNet.IdentityServer.Directory`](https://github.com/auroris/AurorisNet.IdentityServer)
package, which the Add from directory package brings.

## Retiring accounts

Delete in the Users and Members sections retires an account that has signed in or is linked to
a Windows identity. Its username becomes the SID, its email an address at `retired.invalid`, and
it is disabled, while its name and history stay on everything it authored. A new person can then
take the username and email. `Accounts:RetireDeletedUsers` and `RetireDeletedMembers` set to
`false` return Delete to Umbraco's own behavior, from the next delete. The single sign-on
package's [README](https://github.com/auroris/AurorisNet.Umbraco.IdentityServer.SSO) describes
what changes and how to restore an account.
