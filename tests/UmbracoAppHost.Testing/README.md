# Umbraco App Host Testing

`AurorisNet.Umbraco.AppHost.Testing` boots an [Umbraco App Host](https://github.com/auroris/AurorisNet.Umbraco.AppHost) site in a test process, so a host's tests call its pages and endpoints over HTTP.

`TestSite.StartAsync` applies the host's own registrations, as its `Program.cs` does, on Kestrel at a free loopback port with a new SQLite database and instance folder, and returns once the unattended install has finished. The site signs in members and backoffice users, and hands out the endpoint clients from the bundled `AppBlocks.Testing` assembly for its own address and virtual path.

The package references no test framework. Failures throw `TestSiteException`.

## Install

Reference the host project and this package, at the same version as `AurorisNet.Umbraco.AppHost`. `TestSite` applies the host's registrations through the library's generic methods, and a start with another version fails with a message that names the mismatch.

```xml
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.3" />
  <PackageReference Include="AurorisNet.Umbraco.AppHost.Testing" Version="1.0.0-alpha.1" />
  <ProjectReference Include="..\MyHost\MyHost.csproj" />
  <Using Include="UmbracoAppHost.Testing" />
  <Using Include="AppBlocks.Testing" />
</ItemGroup>
```

Add `public partial class Program { }` to the host's `Program.cs` so a test can name its assembly.

```csharp
await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly });
var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot");
using var browser = await site.SignInMemberAsync(member);

var form = site.Forms(browser, pageKey, formKey);
Assert.True((await form.LoadAsync()).IsSuccess);
var saved = await form.CreateAsync(new { customer = "Test order" });
```

`site.Attachments`, `site.Tabular`, `site.Reports` and `site.DownloadPaperReportAsync` call the other AppBlocks endpoints the same way.

## What the site reads from the host project

`StartAsync` finds the host project by walking up from the host assembly's folder in the test output to `<assembly name>.csproj`, or to the one project of that name under the first folder with a `*.slnx`, a `*.sln` or `.git`. Set `TestSiteOptions.ProjectFolder` when the build writes its output outside the repository, or when the host's assembly name differs from its project file's name.

From that folder the site reads `appsettings.json` and `appsettings.<EnvironmentName>.json`, imports the project's uSync export on first boot, and copies its `Views/`.

A host whose settings declare `Kestrel:Endpoints` or `Urls` replaces the free loopback port, and the start fails when they give more than one address. Keep those settings out of the files the test reads, or replace them through `TestSiteOptions.Settings`.

## One site at a time

Umbraco keeps state in static fields, so a process runs one site at a time. `StartAsync` throws while another site of the process is running. Put every test class that starts a site in one collection that runs without parallelization.

A process installs once for each set of options and gives every site its own copy of that install. `TestSiteOptions.ConfigureBuilder` runs on the copy only, so a registration that the first-boot import depends on needs `UseInstallSnapshot = false`.

An install takes most of a site's start, so keep the sets of options few. Every entry of `TestSiteOptions.Settings` is part of the set. Put configuration that only a running site reads, such as which accounts Umbraco manages, in `TestSiteOptions.StartSettings` instead: the install is made without it and shared.

## Logs

A site writes only errors to the console. Its log file under the content root's `umbraco/Logs` has every event, and `TestSiteOptions.Settings[TestSite.ConsoleLogLevelSetting]` sets another level, such as `Information`, for the console.

## Other test runners

The host excludes assemblies whose names start with `xunit.` from Umbraco's type scan. For another runner, add its assembly prefixes through `TestSiteOptions.Settings` at an index of 10 or more, such as `Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:10`. A lower index replaces one of the host's own entries.

## Documentation

[Test a host](https://github.com/auroris/AurorisNet.Umbraco.AppHost/blob/main/docs/getting-started.md#test-a-host) lists every option and `TestSite` member, with a complete test class.

## License

[MIT](https://github.com/auroris/AurorisNet.Umbraco.AppHost/blob/main/LICENSE)
