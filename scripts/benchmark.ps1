param(
    [int]$Runs = 5,
    [string]$DotnetExe,
    [string]$Target,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'outputs' }
if (-not $Target) { $Target = Join-Path $workspace 'work\benchmark-fixture' }
if (-not $DotnetExe) {
    $localSdk = Join-Path $workspace 'work\sdk\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetExe = $localSdk }
    else { $DotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_HOME = Join-Path $workspace 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace 'work\nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:APPDATA = Join-Path $workspace 'work\appdata'
$env:LOCALAPPDATA = Join-Path $workspace 'work\localappdata'
if (-not (Test-Path -LiteralPath $Target)) {
    & (Join-Path $PSScriptRoot 'create-benchmark-fixture.ps1')
    if (-not (Test-Path -LiteralPath $Target)) { throw "Create the requested benchmark target first: $Target" }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $DotnetExe build (Join-Path $workspace 'src\DiskPeek.Cli\DiskPeek.Cli.csproj') -c Release --nologo -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
$cli = Join-Path $workspace 'src\DiskPeek.Cli\bin\Release\net10.0-windows\DiskPeek.Cli.dll'
& $DotnetExe $cli benchmark $Target --runs $Runs --engine directory --cache-state warm --output (Join-Path $OutputDirectory 'benchmark-warm.json')
if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed or accounting totals differ.' }
& $DotnetExe $cli synthetic --count 1000000 --output (Join-Path $OutputDirectory 'benchmark-synthetic.json')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic model benchmark failed.' }
Write-Host 'Reports record the exact target, machine/runtime, sample metrics, and accounting limits.'
Write-Host 'These are warm-cache measurements. Cold-cache reset requires an external controlled setup.'
