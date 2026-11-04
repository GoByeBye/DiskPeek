param([Parameter(Mandatory = $true)][string]$DotnetExe)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repo '.config\dotnet-tools.json') -Raw | ConvertFrom-Json
$version = $manifest.tools.vpk.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The vpk tool manifest must pin an exact stable version.' }
[xml]$appProject = Get-Content -LiteralPath (Join-Path $repo 'src\DiskPeek.App\DiskPeek.App.csproj') -Raw
$library = $appProject.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Velopack"]')
if (-not $library -or $library.Version -cne $version) { throw 'The application Velopack PackageReference and pinned vpk tool must use the same exact version.' }
$toolDirectory = Join-Path $repo 'work\vpk'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$listing = & $DotnetExe tool list --tool-path $toolDirectory
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the workspace-local vpk installation.' }
$installed = ($listing | Select-String -Pattern '^\s*vpk\s+(\S+)\s+').Matches.Groups | Where-Object { $_.Name -eq '1' } | Select-Object -ExpandProperty Value -First 1
if ($installed -and $installed -ne $version) {
    & $DotnetExe tool uninstall vpk --tool-path $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not replace the workspace-local vpk tool version.' }
    $installed = $null
}
if (-not $installed) {
    & $DotnetExe tool install vpk --tool-path $toolDirectory --version $version --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw "Installing workspace-local vpk $version failed." }
}
$verified = & $DotnetExe tool list --tool-path $toolDirectory
if ($LASTEXITCODE -ne 0 -or -not ($verified -match "^\s*vpk\s+$([regex]::Escape($version))\s+")) { throw "The installed vpk tool does not match pinned version $version." }
if (-not (Test-Path -LiteralPath (Join-Path $toolDirectory 'vpk.exe') -PathType Leaf)) { throw 'The vpk command is missing after installation.' }
Write-Host "Using workspace-local vpk $version."
