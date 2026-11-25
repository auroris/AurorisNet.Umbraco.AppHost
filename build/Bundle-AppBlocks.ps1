param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][string] $HostId,
    [Parameter(Mandatory)][string] $Components
)
$ErrorActionPreference = 'Stop'
# NuGet normalizes versions in filenames, including dropping build metadata.
$specs = @{}
foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.nuspec' -Exclude 'bundled.nuspec') {
    $document = [xml](Get-Content -Raw -LiteralPath $file.FullName)
    $specs[$document.package.metadata.id] = $document
}
$spec = $specs[$HostId]
$group = $spec.package.metadata.dependencies.group
$imports = [xml]'<Project />'
$dependencies = @{}
foreach ($dependency in @($group.dependency)) {
    if ($dependency.id -like 'AurorisNet.Umbraco.AppBlocks*') {
        $null = $group.RemoveChild($dependency)
    } else {
        $dependencies[$dependency.id] = $dependency.version
    }
}

foreach ($component in $Components.Split(',')) {
    $id = "AurorisNet.Umbraco.$component"
    $componentSpec = $specs[$id]
    $componentGroup = $componentSpec.package.metadata.dependencies.group
    if ($group.targetFramework -ne $componentGroup.targetFramework) { throw "Framework mismatch for $id." }
    foreach ($dependency in $componentGroup.dependency) {
        if ($dependency.id -like 'AurorisNet.Umbraco.AppBlocks*') { continue }
        if ($dependencies.ContainsKey($dependency.id)) {
            if ($dependencies[$dependency.id] -ne $dependency.version) { throw "Conflicting versions of $($dependency.id) in $HostId and $id." }
        } else {
            $null = $group.AppendChild($spec.ImportNode($dependency, $true))
            $dependencies[$dependency.id] = $dependency.version
        }
    }

    foreach ($file in $componentSpec.package.files.file) {
        $target = $file.target.Replace('\', '/').TrimStart('/')
        if ($target -like 'lib/*') {
            $null = $spec.package.files.AppendChild($spec.ImportNode($file, $true))
        } elseif ($target -match '^(build|buildMultiTargeting|buildTransitive|staticwebassets)/') {
            # Preserve the paths between each Razor SDK manifest and the assets it describes.
            $copy = $spec.ImportNode($file, $true)
            $copy.SetAttribute('target', "bundled/$component/$target")
            $null = $spec.package.files.AppendChild($copy)
            if ($target -in "buildTransitive/$id.props", "buildTransitive/$id.targets") {
                $import = $imports.CreateElement('Import')
                $import.SetAttribute('Project', "../bundled/$component/$target")
                $null = $imports.DocumentElement.AppendChild($import)
            }
        }
    }
}
if ($imports.DocumentElement.HasChildNodes) {
    $path = Join-Path $Directory "$HostId.targets"
    $imports.Save($path)
    $file = $spec.CreateElement('file', $spec.DocumentElement.NamespaceURI)
    $file.SetAttribute('src', $path)
    $file.SetAttribute('target', "buildTransitive/$HostId.targets")
    $null = $spec.package.files.AppendChild($file)
}
$spec.Save((Join-Path $Directory 'bundled.nuspec'))
