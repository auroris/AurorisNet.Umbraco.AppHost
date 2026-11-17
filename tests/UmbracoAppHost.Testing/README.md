# Umbraco App Host Testing

`AurorisNet.Umbraco.AppHost.Testing` boots an [UmbracoAppHost](https://github.com/auroris/UmbracoAppHost) site in a test process. `TestSite.StartAsync` applies the host's own registrations, as its `Program.cs` does, on Kestrel at a free loopback port with a fresh SQLite database and instance folder, and returns once the unattended install has finished. The site signs in members and backoffice users, and hands out the endpoint clients of [AurorisNet.Umbraco.AppBlocks.Testing](https://www.nuget.org/packages/AurorisNet.Umbraco.AppBlocks.Testing) for its own address and virtual path.

The package references no test framework, and failures throw `TestSiteException`. It is proven with xUnit v2 and v3.

## Set up a test project

Reference the host project and this package, at the same version as `AurorisNet.Umbraco.AppHost`. `TestSite` applies the host's registrations through the library's generic methods, so the two must match.

```xml
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.2" />
  <PackageReference Include="AurorisNet.Umbraco.AppHost.Testing" Version="1.0.0-alpha.1" />
  <ProjectReference Include="..\MyHost\MyHost.csproj" />
  <Using Include="UmbracoAppHost.Testing" />
  <Using Include="AppBlocks.Testing" />
</ItemGroup>
```

Add `public partial class Program { }` to the host's `Program.cs` so the test can name its assembly.

```csharp
await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly });
var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot");
using var browser = await site.SignInMemberAsync(member);

var form = site.Forms(browser, pageKey, formKey);
Assert.True((await form.LoadAsync()).IsSuccess);
var saved = await form.CreateAsync(new { customer = "Test order" });
```

`site.Attachments`, `site.Tabular`, `site.Reports` and `site.DownloadPaperReportAsync` drive the other AppBlocks endpoints the same way.

## What the site reads from the host project

`StartAsync` finds the host project by walking up from the host assembly's folder in the test output to `<assembly name>.csproj`, or to the one project of that name under the first folder with a `*.slnx`, a `*.sln` or `.git`. Set `TestSiteOptions.ProjectFolder` when the build writes its output outside the repository, or when the host's assembly name differs from its project file's name. From that folder the site reads `appsettings.json` and `appsettings.<EnvironmentName>.json`, imports the project's uSync export on first boot, and copies its `Views/`.

A host whose settings declare `Kestrel:Endpoints` or `Urls` replaces the free loopback port, and the site then fails to report a single address. Keep those settings out of the files the test reads, or override them through `TestSiteOptions.Settings`.

## One site at a time, and the install snapshot

Umbraco keeps state in static fields, so a process runs one site at a time. `StartAsync` throws while another site of the process is running. Put every test class that starts a site in one collection that runs without parallelization.

A process installs once for each set of options and gives every site its own copy of that install. `TestSiteOptions.ConfigureBuilder` runs on the copy only, so a registration that the first-boot import depends on needs `UseInstallSnapshot = false`.

## Other test runners

The host excludes assemblies starting with `xunit.` from Umbraco's type scan. For another runner, add its assembly prefixes through `TestSiteOptions.Settings` at an index of 10 or more, such as `Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:10`; lower indices replace the host's own entries.

The full guide is [Test a host](https://github.com/auroris/UmbracoAppHost/blob/main/docs/getting-started.md#test-a-host).
