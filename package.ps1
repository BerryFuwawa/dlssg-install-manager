param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (!$projectRoot) { $projectRoot = (Get-Location).Path }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$exe = Join-Path $projectRoot 'dist\DLSSG-Install-Manager.exe'
$manifestPath = Join-Path $projectRoot 'dist\update-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$stream = [IO.File]::OpenRead($exe)
try { $sha=[Security.Cryptography.SHA256]::Create(); try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $sha.Dispose() } } finally { $stream.Dispose() }
if ($hash -ne $manifest.Sha256 -or (Get-Item -LiteralPath $exe).Length -ne $manifest.Size) { throw 'Manifest does not match GUI executable.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $OutputDirectory ("DLSSG-Install-Manager-v" + $manifest.Version + '-portable.zip')
if (Test-Path -LiteralPath $archive) { throw "Package already exists; choose a new output directory: $archive" }
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {
    $files=@('README.md','LICENSE','CHANGELOG.md','dist\DLSSG-Install-Manager.exe','dist\update-manifest.json')
    $files+=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Recurse | ForEach-Object { $_.FullName.Substring($projectRoot.Length + 1) })
    foreach($file in $files) {
        $entry=($file -replace '^dist\\','') -replace '\\','/'
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $projectRoot $file),$entry,[IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
Write-Output "Packaged: $archive"
