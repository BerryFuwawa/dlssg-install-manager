param([Parameter(Mandatory=$true)][string]$LegacyExe)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$directory = Join-Path $projectRoot 'dist\legacy-client'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$old = Join-Path $directory 'DLSSG-Install-Manager.exe'
Copy-Item -LiteralPath $LegacyExe -Destination $old -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$test = Join-Path $directory 'LegacyClientCheck.exe'
& $compiler /nologo /utf8output /codepage:65001 /platform:x64 /target:exe "/out:$test" "/r:$old" /r:System.Net.Http.dll (Join-Path $projectRoot 'tests\LegacyClientCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Legacy client check build failed.' }
& $test (Join-Path $projectRoot 'dist\update-manifest.json') (Join-Path $projectRoot 'dist\DLSSG-Install-Manager.exe')
if ($LASTEXITCODE -ne 0) { throw 'Published v2.0.0 client compatibility check failed.' }
