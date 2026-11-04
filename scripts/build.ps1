param(
    [switch]$SkipTests,
    [switch]$SkipPackage,
    [switch]$SkipInstaller,
    [string]$DotnetExe,
    [string]$OutputDirectory = 'outputs',
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Version must be a semantic version such as 0.1.0 or 0.2.0-preview.1.' }
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory)) }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $repo 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repo 'work\nuget'
$env:APPDATA = Join-Path $repo 'work\build-appdata'
$env:LOCALAPPDATA = Join-Path $repo 'work\build-localappdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$sdkDir = Join-Path $repo 'work\sdk'
$dotnet = if ($DotnetExe) { (Get-Command $DotnetExe -ErrorAction Stop).Source } else { Join-Path $sdkDir 'dotnet.exe' }
if (-not $DotnetExe -and -not (Test-Path -LiteralPath $dotnet)) {
    New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
    $archive = Join-Path $sdkDir 'dotnet-sdk.zip'
    Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip' -OutFile $archive
    $expected = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected) { throw 'SDK SHA-512 mismatch' }
    tar.exe -xf $archive -C $sdkDir
    if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed' }
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "SDK executable not found: $dotnet" }
function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}
$buildOutput = Join-Path $repo 'work\build\bin\'
$buildProperties = @('-p:UseSharedCompilation=false', "-p:Version=$Version", "-p:BaseOutputPath=$buildOutput")
Invoke-Dotnet (@('build', 'DiskPeek.sln', '-c', 'Release', '-m:1') + $buildProperties)
if (-not $SkipTests) {
    $reportPath = Join-Path $output 'verification-results.json'
    $testDll = Join-Path $buildOutput 'Release\net10.0-windows\DiskPeek.Tests.dll'
    Invoke-Dotnet @($testDll, '--report', $reportPath)
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Verification report was not produced.' }
    $verification = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($verification.Failed -ne 0 -or $verification.Passed -lt 1) { throw 'Verification failed or no tests passed.' }
    if ($verification.Skipped -gt 0) { Write-Warning "$($verification.Skipped) verification scenarios were skipped; inspect verification-results.json." }
}
if (-not $SkipPackage) {
    $portable = Join-Path $output 'DiskPeek-portable'
    New-Item -ItemType Directory -Force -Path $portable | Out-Null
    foreach ($project in @('App','Worker','Cli')) {
        Invoke-Dotnet (@('publish', "src\DiskPeek.$project", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $portable, '-p:PublishTrimmed=false', '-m:1') + $buildProperties)
    }
    Copy-Item -LiteralPath LICENSE,README.md,THIRD-PARTY-NOTICES.md -Destination $portable
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $portable 'DiskPeek.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
    if (-not $runtimeVersion) { throw 'Published app does not identify its self-contained runtime.' }
    $runtimePackage = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $portable 'DOTNET-RUNTIME-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $portable 'DOTNET-RUNTIME-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $repo 'docs\licenses\WPF-LICENSE.txt'),(Join-Path $repo 'docs\licenses\WPF-NOTICES.txt') -Destination $portable
    & (Join-Path $PSScriptRoot 'package-source.ps1') -Version $Version -OutputDirectory $output
    $sourceZip = Join-Path $output "DiskPeek-$Version-source.zip"
    Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $portable 'DiskPeek-source.zip')
    if (Test-Path -LiteralPath (Join-Path $repo 'docs')) { Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $portable -Recurse -Force }
    $binaryZip = Join-Path $output "DiskPeek-$Version-win-x64.zip"
    Compress-Archive -Path "$portable\*" -DestinationPath $binaryZip -Force
    $portableAlias = Join-Path $output 'DiskPeek-portable-win-x64.zip'
    Copy-Item -LiteralPath $binaryZip -Destination $portableAlias -Force
    $releaseAssets = @($binaryZip, $sourceZip, $portableAlias)
    if (-not $SkipInstaller) {
        & (Join-Path $PSScriptRoot 'package-installer.ps1') -DotnetExe $dotnet -Version $Version -PortableDirectory $portable -OutputDirectory $output
        $releaseAssets += @((Join-Path $output "DiskPeek-$Version-setup-win-x64.exe"), (Join-Path $output 'DiskPeek-setup-win-x64.exe'),
            (Join-Path $output "DiskPeek-$Version-win-x64-full.nupkg"), (Join-Path $output 'releases.win-x64.json'))
    }
    $releaseAssets | ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
        '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $hash.Path)
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    & (Join-Path $PSScriptRoot 'verify-release.ps1') -Version $Version -Directory $output -PortableOnly:$SkipInstaller
}
