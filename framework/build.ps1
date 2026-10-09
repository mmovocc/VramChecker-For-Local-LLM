param([string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$native = Join-Path $root 'native'
if (!(Test-Path -LiteralPath $native)) { $native = Join-Path (Split-Path $root -Parent) 'native' }
$sample = Join-Path $root 'samples\GlassDemo.cs'
if (!(Test-Path -LiteralPath $sample)) { $sample = Join-Path (Split-Path $root -Parent) 'samples\GlassDemo.cs' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ Build Tools and Windows SDK are required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$batch = Join-Path $output 'build-native.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /LD /DUNICODE /D_UNICODE "$native\LiquidGlass.cpp" /Fo"$output\LiquidGlass.obj" /link /OUT:"$output\LiquidGlass.Native.dll" /IMPLIB:"$output\LiquidGlass.Native.lib" d3d11.lib dxgi.lib dcomp.lib d3dcompiler.lib dwmapi.lib user32.lib gdi32.lib
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $batch -Encoding ascii
& $env:ComSpec /d /c $batch
if ($LASTEXITCODE -ne 0) { throw 'Native framework build failed.' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:library /platform:x64 /optimize+ /codepage:65001 "/out:$output\LiquidGlass.WinForms.dll" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$root\GlassOptions.cs" "$root\GlassRenderer.cs" "$root\GlassForm.cs"
if ($LASTEXITCODE -ne 0) { throw 'Managed framework build failed.' }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output\GlassDemo.exe" "/r:$output\LiquidGlass.WinForms.dll" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sample
if ($LASTEXITCODE -ne 0) { throw 'Demo build failed.' }
Copy-Item -LiteralPath (Join-Path $native 'LiquidGlass.hlsl') -Destination $output -Force
Write-Host "Built framework and demo: $output"
