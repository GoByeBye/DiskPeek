param(
    [string]$DotnetExe,
    [string]$VpkExe,
    [string]$WorkDirectory
)
# Local integration test, intentionally not part of ordinary CI. Requires Windows.
# Creates/removes one unique HKCU uninstall entry. No shortcuts, UAC, or normal
# DiskPeek installation are used. Application/staging files and evidence stay in
# repository work/. The SDK may emit a unique-ID framework log under LocalAppData/velopack.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $DotnetExe) { $DotnetExe = Join-Path $repo 'work\sdk\dotnet.exe' }
if (-not $VpkExe) { $VpkExe = Join-Path $repo 'work\vpk\vpk.exe' }
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $repo ('work\installer-smoke-' + [Guid]::NewGuid().ToString('N')) }
$work = [IO.Path]::GetFullPath($WorkDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'work')) + [IO.Path]::DirectorySeparatorChar
if (-not $work.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Smoke files must stay under repository work/.' }
if (Test-Path -LiteralPath $work) { throw 'Use a fresh smoke directory.' }
foreach ($exe in @($DotnetExe, $VpkExe)) { if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Tool missing: $exe" } }
New-Item -ItemType Directory -Path $work | Out-Null
$install = Join-Path $work 'installed'
$feed = Join-Path $work 'feed'
$id = 'DiskPeek.Smoke.' + [Guid]::NewGuid().ToString('N')
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $id
if (Test-Path -LiteralPath $registration) { throw 'Unique smoke registration already exists or registry access is unavailable.' }
$checks = [ordered]@{}
$oldEnvironment = @{}
foreach ($name in @('DOTNET_ROOT','DOTNET_CLI_HOME','APPDATA','LOCALAPPDATA','NUGET_PACKAGES','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_TELEMETRY_OPTOUT','TEMP','TMP')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit $LASTEXITCODE" }
}
function Invoke-Hidden([string]$Exe, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $repo
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Smoke process timed out.' }
        if ($process.ExitCode -ne 0) { throw "$Exe failed with exit $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
function Probe([string]$Exe, [string]$Command, [string]$Name, [string]$Source = $feed) {
    $output = Join-Path $work ($Name + '.json')
    Invoke-Checked $Exe @($Command, $output, $Source)
    return Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
}
try {
    $env:DOTNET_ROOT = Split-Path -Parent ([IO.Path]::GetFullPath($DotnetExe))
    $env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    $env:APPDATA = Join-Path $work 'appdata'
    $env:LOCALAPPDATA = Join-Path $work 'localappdata'
    $env:NUGET_PACKAGES = Join-Path $repo 'work\nuget'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TEMP = Join-Path $work 'temp'; $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Path $env:TEMP | Out-Null
    foreach ($version in @('0.2.0', '0.2.1')) {
        $publish = Join-Path $work "publish-$version"
        Invoke-Checked $DotnetExe @('publish', (Join-Path $PSScriptRoot 'Probe\Smoke.csproj'), '--artifacts-path', (Join-Path $work 'build'),
            '-r','win-x64','--self-contained','true','-c','Release','-o',$publish,'--configfile',(Join-Path $repo 'NuGet.Config'),
            '--nologo','-p:UseSharedCompilation=false',"-p:Version=$version",'-p:NuGetAudit=false')
        Invoke-Checked $VpkExe @('--skip-updates','--yes','--legacyConsole','pack','--packId',$id,'--packVersion',$version,
            '--packDir',$publish,'--mainExe','DiskPeek.Smoke.exe','--packTitle','DiskPeek isolated installer smoke',
            '--packAuthors','DiskPeek verification','--runtime','win-x64','--channel','win-x64','--outputDir',$feed,
            '--delta','None','--noPortable','--shortcuts','None')
        if ($version -eq '0.2.0') {
            $setup = (Get-ChildItem -LiteralPath $feed -Filter '*Setup.exe' | Select-Object -First 1).FullName
            Copy-Item -LiteralPath $setup -Destination (Join-Path $work 'Smoke-0.2.0-Setup.exe')
        }
    }
    $portable = Probe (Join-Path $work 'publish-0.2.0\DiskPeek.Smoke.exe') 'probe' 'portable'
    $checks.PortableNotInstalled = -not $portable.IsInstalled
    Invoke-Hidden (Join-Path $work 'Smoke-0.2.0-Setup.exe') @('--silent','--installto',$install,'--log',(Join-Path $work 'install.log'))
    $app = Join-Path $install 'current\DiskPeek.Smoke.exe'
    $initial = Probe $app 'probe' 'installed'
    $checks.Installed020 = $initial.CurrentVersion -eq '0.2.0'
    $badFeed = Join-Path $work 'bad-feed'
    New-Item -ItemType Directory -Path $badFeed | Out-Null
    Copy-Item -LiteralPath (Join-Path $feed 'releases.win-x64.json') -Destination $badFeed
    $package = Get-ChildItem -LiteralPath $feed -Filter '*0.2.1*-full.nupkg' | Select-Object -First 1
    $badPackage = Join-Path $badFeed $package.Name
    Copy-Item -LiteralPath $package.FullName -Destination $badPackage
    $stream = [IO.File]::Open($badPackage, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $stream.Position = 4096; $byte = $stream.ReadByte(); $stream.Position = 4096; $stream.WriteByte([byte]($byte -bxor 128)) }
    finally { $stream.Dispose() }
    $corruptOutput = Join-Path $work 'corrupt-download.json'
    & $app stage $corruptOutput $badFeed
    $corruptExit = $LASTEXITCODE
    $corrupt = Get-Content -LiteralPath $corruptOutput -Raw | ConvertFrom-Json
    $checks.CorruptedPackageRejected = $corruptExit -eq 3 -and $corrupt.ErrorType -eq 'Velopack.Exceptions.ChecksumFailedException'
    $checked = Probe $app 'check' 'checked'
    $checks.CheckDoesNotStage = $null -eq $checked.PendingVersion
    $staged = Probe $app 'stage' 'staged'
    $checks.StageDoesNotApply = $staged.CurrentVersion -eq '0.2.0' -and $staged.PendingVersion -eq '0.2.1'
    $restart = Probe $app 'probe' 'restart-before-approval'
    $checks.StartupDoesNotAutoApply = $restart.CurrentVersion -eq '0.2.0' -and $restart.PendingVersion -eq '0.2.1'
    # SDK ApplyUpdatesAndRestart uses nonsilent native update progress. Run the
    # exact SDK call on an isolated desktop, never the user's active desktop.
    if (-not ('DiskPeekSmoke.IsolatedProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'IsolatedProcess.cs') }
    $restartOutput = Join-Path $work 'after-sdk-restart.json'
    $isolated = [DiskPeekSmoke.IsolatedProcess]::Start($app, @('apply-and-restart', $restartOutput, $feed), $repo)
    try {
        if (-not $isolated.WaitForExit(30000) -or $isolated.ExitCode -ne 0) { throw 'SDK apply probe failed to exit successfully.' }
        $timeout = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $restartOutput) -and $timeout.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $restartOutput)) { throw 'SDK did not restart the updated probe within 30 seconds.' }
        $applied = Get-Content -LiteralPath $restartOutput -Raw | ConvertFrom-Json
    } finally { $isolated.Dispose() }
    $checks.ExplicitApply021 = $applied.CurrentVersion -eq '0.2.1' -and $applied.AssemblyVersion -eq '0.2.1'
    $checks.SdkRestart021 = $applied.Command -eq 'probe' -and $applied.CurrentVersion -eq '0.2.1' -and $null -eq $applied.PendingVersion
    $oldFeed = Join-Path $work 'old-feed'
    New-Item -ItemType Directory -Path $oldFeed | Out-Null
    $all = Get-Content -LiteralPath (Join-Path $feed 'releases.win-x64.json') -Raw | ConvertFrom-Json
    @{Assets = @($all.Assets | Where-Object Version -eq '0.2.0')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $oldFeed 'releases.win-x64.json') -Encoding utf8
    $old = Probe $app 'check' 'downgrade-check' $oldFeed
    $checks.DowngradeRejected = $null -eq $old.AvailableVersion
} finally {
    try {
        if (Test-Path -LiteralPath (Join-Path $install 'Update.exe') -PathType Leaf) {
            Invoke-Hidden (Join-Path $install 'Update.exe') @('--silent','--rootDir',$install,'--log',(Join-Path $work 'uninstall.log'),'uninstall')
            $timeout = [Diagnostics.Stopwatch]::StartNew()
            while ((Test-Path -LiteralPath $install) -and $timeout.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 200 }
        }
        $checks.UninstallRegistrationRemoved = -not (Test-Path -LiteralPath $registration)
        $checks.InstalledRootRemoved = -not (Test-Path -LiteralPath $install)
        $passed = $checks.Count -eq 11 -and -not ($checks.Values -contains $false)
        [ordered]@{GeneratedUtc = [DateTime]::UtcNow; VelopackVersion = '1.2.161'; PackageId = $id; Checks = $checks; Passed = $passed} |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'smoke-results.json') -Encoding utf8
        Write-Host "Installer smoke report: $(Join-Path $work 'smoke-results.json')"
    } finally {
        foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    }
}
if (-not $passed) { throw 'One or more installer smoke scenarios failed; inspect smoke-results.json and logs.' }
