param(
    [Parameter(Mandatory = $true)][string]$DotnetExe,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PortableDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid installer version.' }
$portable = [IO.Path]::GetFullPath($PortableDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $portable 'DiskPeek.exe') -PathType Leaf)) { throw 'Publish DiskPeek.exe before creating the installer.' }
if (Test-Path -LiteralPath (Join-Path $portable 'sq.version')) { throw 'The plain portable directory must not contain Velopack installation metadata.' }
& (Join-Path $PSScriptRoot 'install-vpk.ps1') -DotnetExe $DotnetExe

# vpk stamps sq.version into its input. A new work-only copy keeps the ordinary
# portable archive genuinely portable and prevents stale feeds/deltas in new releases.
$stageRoot = Join-Path $repo ('work\installer-packaging\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageRoot 'app'
$nativeOutput = Join-Path $stageRoot 'release'
$temporary = Join-Path $stageRoot 'temp'
New-Item -ItemType Directory -Force -Path $stage,$nativeOutput,$temporary,$output | Out-Null
Get-ChildItem -LiteralPath $portable -Force | Copy-Item -Destination $stage -Recurse -Force
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldDotnetRoot = $env:DOTNET_ROOT
try {
    $env:TEMP = $temporary
    $env:TMP = $temporary
    $env:DOTNET_ROOT = Split-Path -Parent (Get-Command $DotnetExe -ErrorAction Stop).Source
    $arguments = @('pack', '--packId', 'DiskPeek', '--packVersion', $Version,
        '--packDir', $stage, '--mainExe', 'DiskPeek.exe', '--packTitle', 'DiskPeek',
        '--packAuthors', 'DiskPeek contributors', '--channel', 'win-x64', '--runtime', 'win-x64',
        '--delta', 'None', '--noPortable', '--shortcuts', 'StartMenuRoot', '--outputDir', $nativeOutput,
        '--icon', (Join-Path $repo 'assets\diskpeek.ico'))
    & (Join-Path $repo 'work\vpk\vpk.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack installer packaging failed.' }
} finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:DOTNET_ROOT = $oldDotnetRoot
}
$feedPath = Join-Path $nativeOutput 'releases.win-x64.json'
if (-not (Test-Path -LiteralPath $feedPath -PathType Leaf)) { throw 'Velopack did not create the win-x64 feed.' }
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
if (@($feed.Assets).Count -ne 1) { throw 'A fresh installer build must contain exactly one full update package.' }
$asset = $feed.Assets[0]
$packageName = "DiskPeek-$Version-win-x64-full.nupkg"
if ($asset.PackageId -cne 'DiskPeek' -or $asset.Version -cne $Version -or $asset.Type -cne 'Full' -or $asset.FileName -cne $packageName) {
    throw "Unexpected Velopack package identity, version, or channel: $($asset.FileName)"
}
$package = Join-Path $nativeOutput $packageName
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $asset.SHA256 -or
    (Get-Item -LiteralPath $package).Length -ne $asset.Size) { throw 'Velopack feed does not match its full package.' }
$setups = @(Get-ChildItem -LiteralPath $nativeOutput -File -Filter '*Setup.exe')
if ($setups.Count -ne 1) { throw 'Expected exactly one native Velopack Setup executable.' }
$setup = Join-Path $output "DiskPeek-$Version-setup-win-x64.exe"
Copy-Item -LiteralPath $setups[0].FullName -Destination $setup -Force
Copy-Item -LiteralPath $setup -Destination (Join-Path $output 'DiskPeek-setup-win-x64.exe') -Force
Copy-Item -LiteralPath $package -Destination (Join-Path $output $packageName) -Force
Copy-Item -LiteralPath $feedPath -Destination (Join-Path $output 'releases.win-x64.json') -Force
if (Test-Path -LiteralPath (Join-Path $portable 'sq.version')) { throw 'Installer packaging modified the plain portable directory.' }
Write-Host "Created per-user installer and full win-x64 update package for DiskPeek $Version."
Write-Host "Installer staging and native logs: $stageRoot"
