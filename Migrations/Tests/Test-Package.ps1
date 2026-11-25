param([Parameter(Mandatory)][string] $Package)
$ErrorActionPreference = 'Stop'
$export = (Resolve-Path (Join-Path $PSScriptRoot '../*-Current/uSync')).Path
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package).Path)
try {
    $library = @($archive.Entries | Where-Object FullName -like 'lib/*/UmbracoAppHost.dll')
    if ($library.Count -ne 1) { throw "Expected one UmbracoAppHost library in $Package." }
    $framework = $library[0].FullName.Split('/')[1]
    $files = @(Get-ChildItem -LiteralPath $export -Recurse -File -Filter '*.config')
    if (!$files.Count) { throw "No uSync export in $export." }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($export, $file.FullName).Replace('\', '/')
        $expected = (Get-FileHash -LiteralPath $file.FullName).Hash
        foreach ($prefix in 'content/uSync/', "contentFiles/any/$framework/uSync/") {
            $entry = $archive.GetEntry($prefix + $relative)
            if (!$entry) { throw "$Package is missing $prefix$relative." }
            $stream = $entry.Open()
            try { $hash = (Get-FileHash -InputStream $stream).Hash } finally { $stream.Dispose() }
            if ($hash -ne $expected) { throw "$prefix$relative differs from the Current installation." }
        }
    }
    Write-Output "Verified $($files.Count) uSync files in $Package."
} finally { $archive.Dispose() }
