param([string]$Version, [string]$OutputDirectory = 'outputs')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid source package version.' }
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory)) }
$destination = Join-Path $output "DiskPeek-$Version-source.zip"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @()
        foreach ($folder in @('src','tests','scripts','docs','.github')) {
            $folderPath = Join-Path $repo $folder
            if (Test-Path -LiteralPath $folderPath) { $files += Get-ChildItem -LiteralPath $folderPath -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } }
        }
        foreach ($name in @('DiskPeek.sln','Directory.Build.props','global.json','.gitignore','.gitattributes','LICENSE','README.md','THIRD-PARTY-NOTICES.md','NuGet.Config')) {
            $filePath = Join-Path $repo $name
            if (Test-Path -LiteralPath $filePath) { $files += Get-Item -LiteralPath $filePath }
        }
        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\','/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Get-Item -LiteralPath $destination | Select-Object Name,Length
