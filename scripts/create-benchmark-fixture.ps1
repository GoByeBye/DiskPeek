param([int]$Groups = 64, [int]$FilesPerGroup = 256)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($Groups -lt 1 -or $Groups -gt 1000 -or $FilesPerGroup -lt 1 -or $FilesPerGroup -gt 10000) { throw 'Fixture dimensions out of range.' }
$fixture = Join-Path $repo 'work\benchmark-fixture'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$payload = [byte[]]::new(4096)
for ($group = 0; $group -lt $Groups; $group++) {
    $subdirectory = Join-Path $fixture ('group-{0:D3}' -f $group)
    [System.IO.Directory]::CreateDirectory($subdirectory) | Out-Null
    for ($entry = 0; $entry -lt $FilesPerGroup; $entry++) {
        $extension = @('bin','log','json','dat')[$entry % 4]
        $filename = 'entry-{0:D5}.{1}' -f $entry,$extension
        [System.IO.File]::WriteAllBytes((Join-Path $subdirectory $filename), $payload)
    }
}
Write-Output "Fixture: $fixture ($($Groups * $FilesPerGroup) files; 4096 logical bytes each). Existing extra files are retained."
