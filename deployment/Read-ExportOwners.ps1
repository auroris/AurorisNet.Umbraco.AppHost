# Read the same embedded uSync and schema files that the site's build carries. Reading the PE
# resources as data keeps the developer's binaries unlocked and does not start the application.
function Read-EmbeddedXml($Section, [int] $Offset) {
    $reader = $Section.GetReader($Offset, $Section.Length - $Offset)
    return [xml]$reader.ReadUTF8($reader.ReadInt32()).TrimStart([char]0xfeff)
}

function Get-ExportOwners([string] $BuildOutput, [string] $HostAssembly) {
    $owners = @{}
    foreach ($dll in Get-ChildItem -LiteralPath $BuildOutput -Filter '*.dll' -File) {
        $stream = $dll.OpenRead()
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            try { if (!$pe.HasMetadata) { continue } }
            catch [BadImageFormatException] { continue } # A publish can carry native libraries.
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $offsets = @{}
            foreach ($handle in $metadata.ManifestResources) {
                $resource = $metadata.GetManifestResource($handle)
                if ($resource.Implementation.IsNil) { $offsets[$metadata.GetString($resource.Name)] = [int]$resource.Offset }
            }
            $manifest = 'Microsoft.Extensions.FileProviders.Embedded.Manifest.xml'
            if (!$offsets.ContainsKey($manifest)) { continue }
            $section = $pe.GetSectionData($pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
            $files = Read-EmbeddedXml $section $offsets[$manifest]
            foreach ($file in $files.SelectNodes("/Manifest/FileSystem/Directory[@Name='Schema' or @Name='uSync']//File")) {
                if ($file.Name -notlike '*.config' -or $file.Name -eq 'usync.config') { continue }
                $directory = $file.ParentNode
                $path = $file.Name
                while ($directory.ParentNode.LocalName -eq 'Directory') {
                    $path = $directory.GetAttribute('Name') + '/' + $path
                    $directory = $directory.ParentNode
                }
                if ($directory.Name -eq 'uSync' -and $dll.BaseName -eq $HostAssembly) { continue }
                $xml = Read-EmbeddedXml $section $offsets[$file.ResourcePath]
                $owner = [pscustomobject]@{ Assembly = $dll.BaseName; Path = $path; Xml = $xml.DocumentElement.OuterXml }
                $owners[$path] = $owner
                $key = $xml.DocumentElement.GetAttribute('Key')
                if ($key) { $owners[$key] = $owner }
            }
        } finally { $pe.Dispose(); $stream.Dispose() }
    }
    return $owners
}
