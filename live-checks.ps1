param([string]$OutputDirectory, [switch]$Gpu)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (!$OutputDirectory) { throw 'Provide a NEW isolated directory with -OutputDirectory.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $projectRoot 'dist\LiveChecks.exe'
$sources = @('Core.cs','Gpu.cs','Updates.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
& $compiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:exe "/out:$exe" /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Windows.Forms.dll @sources (Join-Path $projectRoot 'tests\LiveChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live checks build failed.' }
$arguments = @($OutputDirectory)
if ($Gpu) { $arguments += '--gpu' }
& $exe @arguments
if ($LASTEXITCODE -ne 0) { throw 'Live package checks failed.' }
