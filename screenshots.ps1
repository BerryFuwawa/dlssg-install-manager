param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'docs\screenshots' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $projectRoot 'dist\Screenshots.exe'
$sources = @('Core.cs','Gpu.cs','Updates.cs','Controls.cs','DpiLayout.cs','FluentForm.cs','MainForm.cs','Program.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
& $compiler /nologo /utf8output /codepage:65001 /optimize+ /platform:x64 /target:exe /main:Screenshots "/out:$exe" "/win32manifest:$projectRoot\app.manifest" /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll @sources (Join-Path $projectRoot 'tests\Screenshots.cs')
if ($LASTEXITCODE -ne 0) { throw 'Screenshot renderer build failed. Run build.ps1 first.' }
& $exe $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Screenshot rendering failed.' }
