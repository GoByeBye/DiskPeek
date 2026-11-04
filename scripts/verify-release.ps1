param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Directory,
    [switch]$PortableOnly
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$directoryPath = [IO.Path]::GetFullPath($Directory)
$packages = @("DiskPeek-$Version-win-x64.zip", "DiskPeek-$Version-source.zip", 'DiskPeek-portable-win-x64.zip')
if (-not $PortableOnly) {
    $packages += @("DiskPeek-$Version-setup-win-x64.exe", 'DiskPeek-setup-win-x64.exe',
        "DiskPeek-$Version-win-x64-full.nupkg", 'releases.win-x64.json')
}
$checksums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $directoryPath 'SHA256SUMS.txt')) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?([^/\\]+)$' -or $checksums.ContainsKey($Matches[2])) { throw 'Malformed or duplicate release checksum entry.' }
    $checksums[$Matches[2]] = $Matches[1]
}
if ($checksums.Count -ne $packages.Count) { throw 'The checksum manifest must cover exactly the selected release assets.' }
foreach ($name in $packages) {
    $path = Join-Path $directoryPath $name
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.LinkType -or -not $checksums.ContainsKey($name)) { throw "Missing, linked, or empty release asset: $name" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $checksums[$name]) { throw "SHA-256 mismatch: $name" }
}
if ($checksums['DiskPeek-portable-win-x64.zip'] -ne $checksums["DiskPeek-$Version-win-x64.zip"]) { throw 'Portable aliases differ.' }
Add-Type -AssemblyName System.IO.Compression
$portableArchive = [IO.Compression.ZipFile]::OpenRead((Join-Path $directoryPath "DiskPeek-$Version-win-x64.zip"))
try {
    if (@($portableArchive.Entries | Where-Object { $_.Name -in @('sq.version', 'Update.exe', '.portable') }).Count -gt 0) { throw 'Plain portable ZIP contains Velopack installation metadata.' }
    foreach ($required in @('DiskPeek.exe','DiskPeek.Worker.exe','DiskPeek.Cli.exe','DiskPeek-source.zip','LICENSE','THIRD-PARTY-NOTICES.md')) {
        if (-not $portableArchive.GetEntry($required)) { throw "Plain portable ZIP is missing $required" }
    }
} finally { $portableArchive.Dispose() }
if ($PortableOnly) { Write-Host 'Verified the three portable/source release assets.'; return }

if ($checksums['DiskPeek-setup-win-x64.exe'] -ne $checksums["DiskPeek-$Version-setup-win-x64.exe"]) { throw 'Installer aliases differ.' }
$setupPath = Join-Path $directoryPath "DiskPeek-$Version-setup-win-x64.exe"
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
$coreVersion = $Version.Split('-')[0]
if ("$($setupInfo.ProductMajorPart).$($setupInfo.ProductMinorPart).$($setupInfo.ProductBuildPart)" -cne $coreVersion) { throw 'Installer product version does not match the release.' }
$feed = Get-Content -LiteralPath (Join-Path $directoryPath 'releases.win-x64.json') -Raw | ConvertFrom-Json
if (@($feed.Assets).Count -ne 1) { throw 'Expected exactly one full update package in the fresh win-x64 feed.' }
$asset = $feed.Assets[0]
$packageName = "DiskPeek-$Version-win-x64-full.nupkg"
if ($asset.PackageId -cne 'DiskPeek' -or $asset.Version -cne $Version -or $asset.Type -cne 'Full' -or $asset.FileName -cne $packageName) { throw 'Update feed package metadata does not match the release.' }
$packagePath = Join-Path $directoryPath $packageName
if ($asset.SHA256 -ne $checksums[$packageName] -or $asset.Size -ne (Get-Item -LiteralPath $packagePath).Length -or
    $asset.SHA1 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash) { throw 'Update feed hashes or size do not match the package.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    foreach ($entry in $archive.Entries) {
        $normalized = $entry.FullName.Replace('\','/')
        if ($normalized -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))') { throw 'Unsafe path in native update package.' }
    }
    $specs = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
    if ($specs.Count -ne 1) { throw 'Update package must contain one root nuspec.' }
    $reader = [IO.StreamReader]::new($specs[0].Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne 'DiskPeek' -or
        $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $Version) { throw 'Native package nuspec identity/version mismatch.' }
    foreach ($field in @(@('channel','win-x64'), @('rid','win-x64'), @('os','win'), @('mainExe','DiskPeek.exe'), @('authors','DiskPeek contributors'))) {
        if ($metadata.SelectSingleNode("*[local-name()='$($field[0])']").InnerText -cne $field[1]) { throw "Native package $($field[0]) mismatch." }
    }
    $main = @($archive.Entries | Where-Object { $_.FullName -match '^lib/[^/]+/DiskPeek\.exe$' })
    if ($main.Count -ne 1) { throw 'Native package must contain exactly one application entry point.' }
    $prefix = $main[0].FullName.Substring(0, $main[0].FullName.Length - 'DiskPeek.exe'.Length)
    foreach ($required in @('DiskPeek.exe','DiskPeek.Worker.exe','DiskPeek.Cli.exe','DiskPeek-source.zip','LICENSE','THIRD-PARTY-NOTICES.md','sq.version')) {
        if (-not $archive.GetEntry($prefix + $required)) { throw "Native installer package is missing $required" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry($prefix + 'sq.version').Open())
    try { [xml]$installedSpec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $installedMetadata = $installedSpec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    foreach ($field in @(@('id','DiskPeek'), @('version',$Version), @('channel','win-x64'), @('rid','win-x64'), @('mainExe','DiskPeek.exe'))) {
        if ($installedMetadata.SelectSingleNode("*[local-name()='$($field[0])']").InnerText -cne $field[1]) { throw "Installed package metadata $($field[0]) mismatch." }
    }
} finally { $archive.Dispose() }
Write-Host "Verified all seven DiskPeek $Version release assets and installer/update metadata."
