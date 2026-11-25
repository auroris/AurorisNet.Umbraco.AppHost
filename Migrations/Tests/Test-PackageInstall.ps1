param([Parameter(Mandatory)][string] $Package)
$ErrorActionPreference = 'Stop'
$packageFile = Get-Item -LiteralPath $Package
$feed = [Security.SecurityElement]::Escape($packageFile.DirectoryName)
$version = $packageFile.Name -replace '^AurorisNet\.Umbraco\.AppHost\.(?=\d)|\.nupkg$'
$scratch = [IO.Directory]::CreateTempSubdirectory('ah-').FullName

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE." }
}

Push-Location -LiteralPath $scratch
try {
    $null = New-Item -ItemType Directory -Path blocked
    # NuGet gives the most specific pattern precedence. No feed can supply an AppBlocks package.
    @"
<configuration>
  <packageSources><clear />
    <add key="bundles" value="$feed" />
    <add key="blocked" value="blocked" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping><clear />
    <packageSource key="bundles"><package pattern="AurorisNet.Umbraco.AppHost" /><package pattern="AurorisNet.Umbraco.AppHost.Testing" /></packageSource>
    <packageSource key="blocked"><package pattern="AurorisNet.Umbraco.AppBlocks*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content NuGet.Config

    foreach ($name in 'Site', 'Helpers') {
        $null = New-Item -ItemType Directory -Path $name
        $id = if ($name -eq 'Site') { 'AurorisNet.Umbraco.AppHost' } else { 'AurorisNet.Umbraco.AppHost.Testing' }
        $project = "$name/$name.csproj"
        @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <CompressionEnabled>false</CompressionEnabled>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="$id" Version="[$version]" /></ItemGroup>
</Project>
"@ | Set-Content $project
        if ($name -eq 'Site') {
            Copy-Item "$PSScriptRoot/PackageSite/Program.cs" $name
            $null = New-Item -ItemType Directory -Path "$name/AppBlocks/Code" -Force
            '#error AppBlocks runtime scripts must be excluded from the site build.' | Set-Content "$name/AppBlocks/Code/Probe.cs"
        } else {
            'Console.WriteLine(typeof(UmbracoAppHost.Testing.TestSite)); Console.WriteLine(typeof(AppBlocks.Testing.TestSiteException));' | Set-Content "$name/Program.cs"
        }
        Invoke-DotNet restore $project --packages "$scratch/packages" --configfile NuGet.Config
        $assets = Get-Content -Raw "$name/obj/project.assets.json" | ConvertFrom-Json -AsHashtable
        if ($assets.libraries.Keys -like 'AurorisNet.Umbraco.AppBlocks*' -or $assets.libraries.Values.type -contains 'project') {
            throw "Unexpected AppBlocks package or project reference in $name."
        }
        $output = "$scratch/out-$name"
        Invoke-DotNet publish $project -c Release --no-restore '-p:UseSharedCompilation=false' -o $output
        Push-Location -LiteralPath $output
        try { Invoke-DotNet "$name.dll" } finally { Pop-Location }
    }
    Write-Host 'Both package consumers passed. Zero AppBlocks NuGet packages restored.'
} finally {
    Pop-Location
    if ((Split-Path $scratch -Parent) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) {
        Remove-Item -Recurse -Force -LiteralPath $scratch
    }
}
