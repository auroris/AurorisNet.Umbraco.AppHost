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

`TestSiteOptions.VirtualPath` boots a test site under a prefix, and the clients it hands out use
it; see [Test a host](getting-started.md#test-a-host). The browser tests of the sign-in and the
backoffice under a prefix belong to the fix package's repository.
