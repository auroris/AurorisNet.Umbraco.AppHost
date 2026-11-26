# Developing

Requires the .NET 10 SDK, Node 20+, PowerShell 7, and sibling checkouts beside this repo:

| Sibling checkout | Folder property | Source switch (default `true`) |
|---|---|---|
| `../AurorisNet.Umbraco.AppBlocks` | `AppBlocksRoot` | Always built from source |
| `../AurorisNet.Umbraco.BreakGlass` | `BreakGlassRoot` | `UseBreakGlassSource` |
| `../AurorisNet.Umbraco.IdentityServer.SSO` | `IdentityServerSsoRoot` | `UseIdentityServerSsoSource` |
| `../AurorisNet.Umbraco.IdentityServer.Directory` | `IdentityServerDirectoryRoot` | `UseIdentityServerDirectorySource` |

Pass `-p:UseBreakGlassSource=false` (etc.) to use the NuGet versions pinned in `Directory.Packages.props`. The build verifies that `AppBlocks` pins the same `Umbraco.Cms`, `Microsoft.Data.Sqlite`, and `Microsoft.Extensions.FileProviders.Embedded` versions as AppHost.

## Build, test, and pack

```powershell
dotnet build UmbracoAppHost.slnx
dotnet test --project tests/UmbracoAppHost.Tests/UmbracoAppHost.Tests.csproj
./tests/test-parallel.ps1                    # Multi-process run (add -RecordTimings to update timings)
cd Frontend; npm ci; npm run check; npm test; cd ..

dotnet pack src/UmbracoAppHost/UmbracoAppHost.csproj -c Release -o artifacts/packages
dotnet pack tests/UmbracoAppHost.Testing/UmbracoAppHost.Testing.csproj -c Release -o artifacts/packages
./Migrations/Tests/Test-PackageInstall.ps1 (Get-Item artifacts/packages/AurorisNet.Umbraco.AppHost.[0-9]*.nupkg).FullName
```

Local-only checks:
- `deployment/test-sites.ps1` — publish, create two sites, update one, rehearse a second release.
- `deployment/test-downstream.ps1` — test a downstream consumer project update.
- `tests/UmbracoAppHost.ProviderTests` — PostgreSQL/Oracle tests via `UMBRACOAPPHOST_TEST_POSTGRES`, `UMBRACOAPPHOST_TEST_ORACLE`, or `UMBRACOAPPHOST_TEST_ORACLE11G`.

## Releases

CI and Release check out `AppBlocks` `main` and restore the other sibling packages from NuGet. Both `AurorisNet.Umbraco.AppHost` and `AurorisNet.Umbraco.AppHost.Testing` publish under one version. See the shared [release guide](https://github.com/auroris/AurorisNet.Build/blob/main/docs/releasing.md).
