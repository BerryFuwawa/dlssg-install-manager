param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (!$projectRoot) { $projectRoot = (Get-Location).Path }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x compiler not found.' }
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$helper = Join-Path $dist 'UpdateHelper.exe'
$exe = Join-Path $dist 'DLSSG-Install-Manager.exe'
# Code-drawn icon: no downloaded artwork or runtime image dependencies.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$canvas = [Drawing.Graphics]::FromImage($bitmap)
$canvas.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$canvas.Clear([Drawing.Color]::FromArgb(25,33,54))
$purple = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0,103,192))
$mint = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(77,222,180))
$white = New-Object Drawing.Pen ([Drawing.Color]::White),3
$canvas.FillRectangle($purple,10,12,30,30)
$canvas.FillRectangle($mint,25,27,29,26)
$canvas.DrawRectangle($white,17,20,30,28)
$icon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconStream = [IO.File]::Create((Join-Path $dist 'app.ico'))
try { $icon.Save($iconStream) } finally { $iconStream.Dispose(); $icon.Dispose(); $canvas.Dispose(); $bitmap.Dispose(); $purple.Dispose(); $mint.Dispose(); $white.Dispose() }
$common = @('/nologo','/utf8output','/codepage:65001','/optimize+','/platform:x64')
& $compiler @common /target:winexe "/out:$helper" /r:System.Windows.Forms.dll (Join-Path $projectRoot 'src\UpdateHelper.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update helper build failed.' }
$sources = @('Core.cs','Gpu.cs','Updates.cs','Controls.cs','DpiLayout.cs','FluentForm.cs','MainForm.cs','Program.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
$references = @('/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $compiler @common /target:winexe "/out:$exe" "/win32manifest:$projectRoot\app.manifest" "/win32icon:$dist\app.ico" "/resource:$helper,UpdateHelper.exe" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }
$stream = [IO.File]::OpenRead($exe)
try { $sha = [Security.Cryptography.SHA256]::Create(); try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $sha.Dispose() } } finally { $stream.Dispose() }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -replace '\.0$',''
$manifest = @{Schema=1;Version=$version;File='DLSSG-Install-Manager.exe';Sha256=$hash;Size=(Get-Item -LiteralPath $exe).Length} | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $dist 'update-manifest.json'), $manifest, (New-Object Text.UTF8Encoding $false))
Write-Output "Built GUI v$version : $exe"
if ($Test) {
    $testSources = @('Core.cs','Gpu.cs','Updates.cs') | ForEach-Object { Join-Path $projectRoot "src\$_" }
    $testExe = Join-Path $dist 'Tests.exe'
    $fixtureOld = Join-Path $dist 'fixtures\old'
    $fixtureNew = Join-Path $dist 'fixtures\new'
    New-Item -ItemType Directory -Path $fixtureOld,$fixtureNew -Force | Out-Null
    & $compiler @common /target:exe "/out:$fixtureOld\DLSSG-Install-Manager.exe" "/resource:$helper,UpdateHelper.exe" @references @testSources (Join-Path $projectRoot 'tests\UpdateFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Old update fixture build failed.' }
    & $compiler @common /target:exe /define:NEXT "/out:$fixtureNew\DLSSG-Install-Manager.exe" @references @testSources (Join-Path $projectRoot 'tests\UpdateFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'New update fixture build failed.' }
    & $compiler @common /target:exe "/out:$testExe" @references @testSources (Join-Path $projectRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $uiExe = Join-Path $dist 'UiTests.exe'
    & $compiler @common /target:exe /main:UiTests "/out:$uiExe" "/win32manifest:$projectRoot\app.manifest" @references @sources (Join-Path $projectRoot 'tests\UiTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
    & $uiExe (Join-Path $dist 'ui-update-test.png')
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
}
