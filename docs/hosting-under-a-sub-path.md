# Hosting under a sub-path

A site can run under a prefix, such as an IIS application at `/portal` beneath another site's
root. One setting covers the whole site, the backoffice included:

```json
{
  "Umbraco": {
    "CMS": {
      "Hosting": {
        "ApplicationVirtualPath": "/portal"
      }
    }
  }
}
```

Put it in the site's `app/appsettings.json` or `instance.json`. Umbraco removes the prefix from
incoming addresses before it routes content and adds it to the links it generates. The site and
its backoffice then answer at `/portal/` and `/portal/umbraco`.

## Running several installations on one hostname

[`AurorisNet.Umbraco.ApplicationVirtualPathFix`](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix) gives each installation its own cookie names and backoffice tab messages (and AppHost registers its member sign-out cookie with it). For example,
an editor can sign in to both `/site-a/umbraco` and `/site-b/umbraco` in one browser. Signing out of
`/site-a` signs out its other backoffice tabs while the `/site-b` session stays active.

The suffix combines a cookie-safe version of `Umbraco:CMS:Hosting:SiteName` with 12 hex
characters derived from `Umbraco:CMS:Global:Id`, for example
`__Host-umbRefreshToken-portal-a1b2c3d4e5f6`. The application name supplies the label when
`SiteName` is empty. New sites created by `new-site.ps1` use their `-Name` as the label.
AppHost persists the installation ID in `instance.json`. Keep it across updates and replicas
of the same site, and give independently cloned sites their own installation IDs.
Hosts using the lower-level `IUmbracoBuilder` entry point supply their own `Global:Id`.
If they omit it, the suffix uses the physical and virtual paths and can change when the app moves.

Cookie paths are case-sensitive. A bookmark such as `/PORTAL/orders` redirects to
`/portal/orders` when the configured prefix is `/portal`. The redirect preserves the request
method and query string so forms and sign-in callbacks reach the same session.

Users sign in again after the first deployment with these names, or after changing the site
name or installation ID. Older sites on the hostname can continue using their existing cookies
during rollout. HTTPS cookies with the `__Host-` prefix must use `Path=/`, so the browser sends
them to every path on that hostname. Their size still counts toward request-header limits when
a user signs in to many sites at once.

### HTTP 400 during domain sign-in on IIS

A browser can carry cookies from several live sites and from previous installations at the
same address. Domain sign-in also adds an OpenID Connect authorization URL and, when Windows
answers the challenge, an authentication header. HTTP.sys counts the request line and all
headers against a machine-wide limit of 16 KiB by default. It can reject the request before
it reaches AppHost. [HTTP.sys settings](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/health-diagnostic-performance/httpsys-registry-windows)

For **Bad Request – Request Too Long**, check the matching request in
`%SystemRoot%\System32\LogFiles\HTTPERR`. A 400 with reason `RequestLength` points to the
combined size. `FieldLength` points to a single oversized field. A private window can help
identify the contribution of cookies, but verification should include the original cookie load.
[Diagnosing HTTP 400 on IIS](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/site-behavior-performance/troubleshoot-http-400-errors-in-iis)

On a multi-site test VM, a failing `/connect/authorize` request had 23 cookies totaling 14,192
bytes and a 1,393-byte request target. For that workload, set the total allowance to 32 KiB
in an elevated PowerShell session. The per-field allowance stays at its 16 KiB default.

```powershell
$httpParameters = 'HKLM:\SYSTEM\CurrentControlSet\Services\HTTP\Parameters'
New-ItemProperty -LiteralPath $httpParameters -Name MaxRequestBytes -PropertyType DWord -Value 32768 -Force
```

Schedule the HTTP service restart for the whole VM. Record which dependent services are
running, restart HTTP, and bring those services back up. On a typical IIS host these include IIS
(`W3SVC`), WinRM and the Print Spooler. Verify through the member page's **Sign in with
domain** button. A 16,447-byte request with the same cookie count and sizes returned the
Windows authentication challenge (`401` with `WWW-Authenticate: Negotiate`). Completing that
challenge with a domain account verifies member sign-in end to end.
[HTTP service restart requirements](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/www-authentication-authorization/http-bad-request-response-kerberos)

## What applies the prefix

Umbraco builds many backoffice, sign-in and asset addresses from the host's root. The
[`AurorisNet.Umbraco.ApplicationVirtualPathFix`](https://github.com/auroris/AurorisNet.Umbraco.ApplicationVirtualPathFix)
package corrects them. The library references it, and it registers itself through Umbraco's
composers, so a site needs no code for it. Its README lists the hosting requirements, such as an
IIS child application or a reverse proxy that forwards the prefix, the health check that
confirms requests arrive under the path, and every address it corrects.

The host and AppBlocks apply the same setting to their own addresses:

| Part | Behavior under a prefix |
|---|---|
| Block preview stylesheets | `Hosting/BlockPreviews.cs` adds the prefix to each root-relative stylesheet in the `BlockPreview` settings, so `/dist/css/site.css` loads from `/portal/dist/css/site.css`. |
| Menu tokens and sign-out | `{{user.backoffice}}` and `{{user.signout}}` resolve under the request's path base. |
| Welcome dashboard | The dashboard reads its report from the backoffice routes under the prefix. |
| Report definitions | Definitions are written as if the site were at the root. The Interactive Reports companion adds the prefix to a root-absolute edit link and replaces `{SiteRoot}` in a definition's SQL where a URL starts. |
| Form, attachment, tabular and paper report endpoints | AppBlocks' runtime calls them under the request's path base. |

## Upgrading Umbraco

`Directory.Packages.props` pins the fix package beside `Umbraco.Cms`. Each version of the
package supports a range of Umbraco releases, listed in its README. Before raising `Umbraco.Cms`,
check that the new release is in that range, and raise the package with it when needed.

## Testing under a prefix

`TestSiteOptions.VirtualPath` boots a test site under a prefix, and its clients use that prefix.
See [Test a host](getting-started.md#test-a-host). The fix package's repository covers the
backoffice under a prefix. AppHost's [two-site browser test](../tests/ui/site-cookie-isolation.browser.test.mjs)
checks sign-in, token refresh, logout and restart with two installations on one HTTPS origin.
