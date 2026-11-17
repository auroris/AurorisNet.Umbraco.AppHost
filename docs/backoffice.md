# Login screen and backoffice

The library adds a Welcome dashboard to the backoffice, gives a site or theme library a way to
brand the login screen, and stops Umbraco's telemetry reports. Its backoffice extensions are in
`src/UmbracoAppHost/wwwroot/App_Plugins/`.

## Welcome dashboard

The **Back Office Experience** package (`App_Plugins/BackOfficeExperience/`) registers a
**Welcome** dashboard in the Content section, ahead of Redirect URL Management, and removes
Umbraco's news dashboard.

The dashboard shows the `umbraco-recent-pages` Interactive Report under the heading **Latest
Changes**: the pages changed most recently, newest save first, ten rows to a page. Each row has
the page name, its full path in the content tree, the last editor and the save time, with an
**Edit page** link that opens the document in the same tab. Unpublished changes are included.
Search, sorting, filters and page size are the report's own controls.

The report reads through the backoffice routes `/umbraco/reports` and
`/umbraco/report-download`, which accept the backoffice session alone. It needs a user with
access to the Content section and lists only pages within the user's start nodes that the user
may browse. It is one of the [built-in reports](interactive-reports.md#built-in-reports), which
`AppBlocks:IncludeBackOfficeReports` turns on.

## Brand the login screen

A site without branding shows Umbraco's own login screen. A site or a
[theme library](themes.md#a-theme-library) brands it through Umbraco's extension points, from a
package manifest that sets `allowPublicAccess` to `true` so the login page loads it before
sign-in.

**Logo and panel image.** Put the images under `wwwroot/umbraco/` and register them:

```csharp
builder.Services.AddLoginScreen(builder.Config, "mytheme/logo.svg", "mytheme/panel.jpg");
```

`AddLoginScreen` (`LoginScreen.cs`) sets `Umbraco:CMS:Content:LoginLogoImage`,
`LoginLogoImageAlternative` and `LoginBackgroundImage` to these paths, which are relative to
`wwwroot/umbraco/`. A setting the site configures itself keeps its value, so a theme brands a
site by default and the site can still replace an image. `LoginBackgroundImage` is the picture
inside the rounded panel beside the sign-in form, and the page background is set separately.

**Greeting.** Add a `localization` extension for each culture that replaces the greeting keys of
Umbraco's `login` section.

**Page background and sign-in panel.** Add an `appEntryPoint` extension whose script loads a
stylesheet. The login layout reads custom properties such as `--umb-login-background` from the
document root. The sign-in panel is inside the layout's shadow root, where page CSS has no
effect, so the script adopts a stylesheet into that root to style the panel. Set the background
on the shadow root's host in the same stylesheet: the sign-in view inside the backoffice
application uses another element, which does not read the background property.

## Telemetry

The library references
[`AurorisNet.Umbraco.DisableTelemetry`](https://github.com/auroris/AurorisNet.Umbraco.DisableTelemetry),
pinned in `Directory.Packages.props`. Its composer registers itself. It removes Umbraco's
scheduled telemetry job, so the site sends no report at any telemetry level, completes installer
and upgrade reports on the server, and serves the news dashboard without fetching a remote feed.
The package's README lists its supported Umbraco releases.

## Block previews

`App_Plugins/BlockPreviews.Host/` loads the preview styles for the block editors. See
[Block previews](block-previews.md).
