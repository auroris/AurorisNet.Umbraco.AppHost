# App Host Testing

Boots an [App Host](https://github.com/auroris/AurorisNet.Umbraco.AppHost) site in-process on Kestrel at a free loopback port with a fresh SQLite database, signs in members and backoffice users, and exposes the bundled `AppBlocks.Testing` HTTP clients (`Forms`, `Attachments`, `Tabular`, `Reports`, `DownloadPaperReportAsync`). Framework-agnostic (throws `TestSiteException`).

## Install

Match the version of `AurorisNet.Umbraco.AppHost` referenced by your host project, and add `public partial class Program { }` to the host's `Program.cs`:

```xml
<ItemGroup>
  <PackageReference Include="AurorisNet.Umbraco.AppHost.Testing" Version="1.0.0-alpha.1" />
  <ProjectReference Include="..\MyHost\MyHost.csproj" />
  <Using Include="UmbracoAppHost.Testing" />
  <Using Include="AppBlocks.Testing" />
</ItemGroup>
```

```csharp
await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly });
var member = site.CreateMember("pilot", "pilot@example.invalid", "Test pilot");
using var browser = await site.SignInMemberAsync(member);

var form = site.Forms(browser, pageKey, formKey);
await form.LoadAsync();
var saved = await form.CreateAsync(new { customer = "Test order" });
```

## Key notes

- **One site per process:** Put test classes that call `StartAsync` in a single non-parallel collection.
- **Shared install snapshots:** Installs are cached per unique `TestSiteOptions.Settings`. Put runtime-only overrides in `TestSiteOptions.StartSettings` to reuse the cached install; set `UseInstallSnapshot = false` if `ConfigureBuilder` affects first-boot imports.
- **Host project discovery:** Walks up from the test output directory to `<AssemblyName>.csproj` (override via `TestSiteOptions.ProjectFolder`) to load `appsettings*.json`, uSync exports, and `Views/`. Avoid hardcoded `Urls`/`Kestrel:Endpoints` in test settings.
- **Logs & non-xUnit runners:** Full logs write to `umbraco/Logs` (set `TestSiteOptions.Settings[TestSite.ConsoleLogLevelSetting]` for console output). For runners other than xUnit, exclude their assembly prefix at index `10+` in `Umbraco:CMS:TypeFinder:AdditionalAssemblyExclusionEntries:10`.

See [Test a host](https://github.com/auroris/AurorisNet.Umbraco.AppHost/blob/main/docs/getting-started.md#test-a-host) and [MIT License](https://github.com/auroris/AurorisNet.Umbraco.AppHost/blob/main/LICENSE).
